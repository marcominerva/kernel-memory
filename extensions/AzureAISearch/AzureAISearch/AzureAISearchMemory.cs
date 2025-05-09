﻿// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Identity;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.Logging;
using Microsoft.KernelMemory.AI;
using Microsoft.KernelMemory.Diagnostics;
using Microsoft.KernelMemory.DocumentStorage;
using Microsoft.KernelMemory.MemoryStorage;
using AISearchOptions = Azure.Search.Documents.SearchOptions;

namespace Microsoft.KernelMemory.MemoryDb.AzureAISearch;

/// <summary>
/// Azure AI Search connector for Kernel Memory
/// TODO:
/// * support semantic search
/// * support custom schema
/// * support custom Azure AI Search logic
/// </summary>
public class AzureAISearchMemory : IMemoryDb, IMemoryDbUpsertBatch
{
    private readonly ITextEmbeddingGenerator _embeddingGenerator;
    private readonly ILogger<AzureAISearchMemory> _log;
    private readonly bool _useHybridSearch;
    private readonly bool _useStickySessions;

    /// <summary>
    /// Create a new instance
    /// </summary>
    /// <param name="config">Azure AI Search configuration</param>
    /// <param name="embeddingGenerator">Text embedding generator</param>
    /// <param name="loggerFactory">Application logger factory</param>
    public AzureAISearchMemory(
        AzureAISearchConfig config,
        ITextEmbeddingGenerator embeddingGenerator,
        ILoggerFactory? loggerFactory = null)
    {
        this._embeddingGenerator = embeddingGenerator;
        this._log = (loggerFactory ?? DefaultLogger.Factory).CreateLogger<AzureAISearchMemory>();
        this._useHybridSearch = config.UseHybridSearch;
        this._useStickySessions = config.UseStickySessions;

        if (string.IsNullOrEmpty(config.Endpoint))
        {
            this._log.LogCritical("Azure AI Search Endpoint is empty");
            throw new ConfigurationException($"Azure AI Search: {nameof(config.Endpoint)} is empty");
        }

        if (this._embeddingGenerator == null)
        {
            throw new ConfigurationException($"Azure AI Search: {nameof(this._embeddingGenerator)} is not configured");
        }

        var clientOptions = GetClientOptions(config);
        switch (config.Auth)
        {
            case AzureAISearchConfig.AuthTypes.AzureIdentity:
                this._adminClient = new SearchIndexClient(
                    new Uri(config.Endpoint),
                    new DefaultAzureCredential(),
                    clientOptions);
                break;

            case AzureAISearchConfig.AuthTypes.APIKey:
                if (string.IsNullOrEmpty(config.APIKey))
                {
                    this._log.LogCritical("Azure AI Search API key is empty");
                    throw new ConfigurationException($"Azure AI Search: {nameof(config.APIKey)} is empty");
                }

                this._adminClient = new SearchIndexClient(
                    new Uri(config.Endpoint),
                    new AzureKeyCredential(config.APIKey),
                    clientOptions);
                break;

            case AzureAISearchConfig.AuthTypes.ManualTokenCredential:
                this._adminClient = new SearchIndexClient(
                    new Uri(config.Endpoint),
                    config.GetTokenCredential(),
                    clientOptions);
                break;

            default:
            case AzureAISearchConfig.AuthTypes.Unknown:
                this._log.LogCritical("Azure AI Search authentication type '{0}' undefined or not supported", config.Auth);
                throw new DocumentStorageException($"Azure AI Search authentication type '{config.Auth}' undefined or not supported");
        }
    }

    /// <inheritdoc />
    public Task CreateIndexAsync(string index, int vectorSize, CancellationToken cancellationToken = default)
    {
        // Vectors cannot be less than 2 - TODO: use different index schema
        vectorSize = Math.Max(2, vectorSize);
        return this.CreateIndexAsync(index, AzureAISearchMemoryRecord.GetSchema(vectorSize), cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<string>> GetIndexesAsync(CancellationToken cancellationToken = default)
    {
        var indexesAsync = this._adminClient.GetIndexesAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<string>();
        await foreach (SearchIndex? index in indexesAsync.ConfigureAwait(false))
        {
            result.Add(index.Name);
        }

        return result;
    }

    /// <inheritdoc />
    public Task DeleteIndexAsync(string index, CancellationToken cancellationToken = default)
    {
        index = this.NormalizeIndexName(index);
        return this._adminClient.DeleteIndexAsync(index, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<string> UpsertAsync(string index, MemoryRecord record, CancellationToken cancellationToken = default)
    {
        var result = this.UpsertBatchAsync(index, [record], cancellationToken);
        var id = await result.SingleAsync(cancellationToken).ConfigureAwait(false);
        return id;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> UpsertBatchAsync(
        string index,
        IEnumerable<MemoryRecord> records,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var client = this.GetSearchClient(index);
        var localRecords = records.Select(AzureAISearchMemoryRecord.FromMemoryRecord);

        try
        {
            await client.IndexDocumentsAsync(
                IndexDocumentsBatch.Upload(localRecords),
                new IndexDocumentsOptions { ThrowOnAnyError = true },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException e) when (IsIndexNotFoundException(e))
        {
            throw new IndexNotFoundException(e.Message, e);
        }

        foreach (var record in records)
        {
            yield return record.Id;
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<(MemoryRecord, double)> GetSimilarListAsync(
        string index,
        string text,
        ICollection<MemoryFilter>? filters = null,
        double minRelevance = 0,
        int limit = 1,
        bool withEmbeddings = false,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var client = this.GetSearchClient(index);

        Embedding textEmbedding = await this._embeddingGenerator.GenerateEmbeddingAsync(text, cancellationToken).ConfigureAwait(false);
        VectorizedQuery vectorQuery = new(textEmbedding.Data)
        {
            Fields = { AzureAISearchMemoryRecord.VectorField },
            // Exhaustive search is a brute force comparison across all vectors,
            // ignoring the index, which can be much slower once the index contains a lot of data.
            // TODO: allow clients to manage this value either at configuration or run time.
            Exhaustive = false
        };

        AISearchOptions options = new()
        {
            VectorSearch = new()
            {
                Queries = { vectorQuery },
                // Default, applies the vector query AFTER the search filter
                FilterMode = VectorFilterMode.PreFilter
            }
        };
        options = this.PrepareSearchOptions(options, withEmbeddings, filters, limit);

        if (limit > 0)
        {
            vectorQuery.KNearestNeighborsCount = limit;
            this._log.LogDebug("KNearestNeighborsCount: {0}", limit);
        }

        Response<SearchResults<AzureAISearchMemoryRecord>>? searchResult = null;
        try
        {
            var keyword = this._useHybridSearch ? text : null;
            searchResult = await client
                .SearchAsync<AzureAISearchMemoryRecord>(keyword, options, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (RequestFailedException e) when (e.Status == 404)
        {
            this._log.LogWarning("Not found: {0}", e.Message);
            // Index not found, no data to return
        }

        if (searchResult == null) { yield break; }

        var minDistance = this._useHybridSearch ? minRelevance : CosineSimilarityToScore(minRelevance);
        var count = 0;
        await foreach (SearchResult<AzureAISearchMemoryRecord>? doc in searchResult.Value.GetResultsAsync().ConfigureAwait(false))
        {
            if (doc == null || doc.Score < minDistance) { continue; }

            MemoryRecord memoryRecord = doc.Document.ToMemoryRecord(withEmbeddings);

            var documentScore = this._useHybridSearch ? doc.Score ?? 0 : ScoreToCosineSimilarity(doc.Score ?? 0);
            yield return (memoryRecord, documentScore);

            // Stop after returning the amount requested, even if storage is returning more records
            if (limit > 0 && ++count >= limit)
            {
                break;
            }
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<MemoryRecord> GetListAsync(
        string index,
        ICollection<MemoryFilter>? filters = null,
        int limit = 1,
        bool withEmbeddings = false,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var client = this.GetSearchClient(index);

        AISearchOptions options = this.PrepareSearchOptions(null, withEmbeddings, filters, limit);

        Response<SearchResults<AzureAISearchMemoryRecord>>? searchResult = null;
        try
        {
            searchResult = await client
                .SearchAsync<AzureAISearchMemoryRecord>(null, options, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (RequestFailedException e) when (e.Status == 404)
        {
            this._log.LogWarning("Not found: {0}", e.Message);
            // Index not found, no data to return
        }

        if (searchResult == null) { yield break; }

        var count = 0;
        await foreach (SearchResult<AzureAISearchMemoryRecord>? doc in searchResult.Value.GetResultsAsync().ConfigureAwait(false))
        {
            yield return doc.Document.ToMemoryRecord(withEmbeddings);

            // Stop after returning the amount requested, even if storage is returning more records
            if (limit > 0 && ++count >= limit)
            {
                break;
            }
        }
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string index, MemoryRecord record, CancellationToken cancellationToken = default)
    {
        string id = AzureAISearchMemoryRecord.FromMemoryRecord(record).Id;
        var client = this.GetSearchClient(index);

        try
        {
            this._log.LogDebug("Deleting record {0} from index {1}", id, index);
            Response<IndexDocumentsResult>? result = await client.DeleteDocumentsAsync(
                    AzureAISearchMemoryRecord.IdField,
                    [id],
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            this._log.LogTrace("Delete response status: {0}, content: {1}", result.GetRawResponse().Status, result.GetRawResponse().Content.ToString());
        }
        catch (RequestFailedException e) when (e.Status == 404)
        {
            this._log.LogTrace("Index {0} record {1} not found, nothing to delete", index, id);
        }
    }

    #region private

    // private async Task<AzureAISearchMemoryRecord?> GetAsync(string indexName, string id, CancellationToken cancellationToken = default)
    // {
    //     try
    //     {
    //         Response<AzureAISearchMemoryRecord>? result = await this.GetSearchClient(indexName)
    //             .GetDocumentAsync<AzureAISearchMemoryRecord>(id, cancellationToken: cancellationToken)
    //             .ConfigureAwait(false);
    //
    //         return result?.Value;
    //     }
    //     catch (Exception e)
    //     {
    //         this._log.LogError(e, "Failed to fetch record");
    //         return null;
    //     }
    // }

    private async Task CreateIndexAsync(string index, MemoryDbSchema schema, CancellationToken cancellationToken = default)
    {
        if (await this.DoesIndexExistAsync(index, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var indexSchema = this.PrepareIndexSchema(index, schema);

        try
        {
            await this._adminClient.CreateIndexAsync(indexSchema, cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException e) when (e.Status == 409)
        {
            this._log.LogWarning("Index already exists, nothing to do: {0}", e.Message);
        }
    }

    private async Task<bool> DoesIndexExistAsync(string index, CancellationToken cancellationToken = default)
    {
        string normalizeIndexName = this.NormalizeIndexName(index);

        var indexesAsync = this._adminClient.GetIndexesAsync(cancellationToken).ConfigureAwait(false);
        await foreach (SearchIndex? searchIndex in indexesAsync.ConfigureAwait(false))
        {
            if (searchIndex != null && string.Equals(searchIndex.Name, normalizeIndexName, StringComparison.OrdinalIgnoreCase)) { return true; }
        }

        return false;
    }

    /// <summary>
    /// Index names cannot contain special chars. We use this rule to replace a few common ones
    /// with an underscore and reduce the chance of errors. If other special chars are used, we leave it
    /// to the service to throw an error.
    /// Note:
    /// - replacing chars introduces a small chance of conflicts, e.g. "the-user" and "the_user".
    /// - we should consider whether making this optional and leave it to the developer to handle.
    /// </summary>
    private static readonly Regex s_replaceIndexNameCharsRegex = new(@"[\s|\\|/|.|_|:]");

    private readonly ConcurrentDictionary<string, SearchClient> _clientsByIndex = new();

    private readonly SearchIndexClient _adminClient;

    /// <summary>
    /// Get a search client for the index specified.
    /// Note: the index might not exist, but we avoid checking everytime and the extra latency.
    /// </summary>
    /// <param name="index">Index name</param>
    /// <returns>Search client ready to read/write</returns>
    private SearchClient GetSearchClient(string index)
    {
        var normalIndexName = this.NormalizeIndexName(index);

        if (index != normalIndexName) { this._log.LogTrace("Preparing search client, index name '{0}' normalized to '{1}'", index, normalIndexName); }
        else { this._log.LogTrace("Preparing search client, index name '{0}'", normalIndexName); }

        // Search an available client from the local cache
        if (!this._clientsByIndex.TryGetValue(normalIndexName, out SearchClient? client))
        {
            client = this._adminClient.GetSearchClient(normalIndexName);
            this._clientsByIndex[normalIndexName] = client;
        }

        return client;
    }

    private static bool IsIndexNotFoundException(RequestFailedException e)
    {
        return e.Status == 404
               && e.Message.Contains("index", StringComparison.OrdinalIgnoreCase)
               && e.Message.Contains("not found", StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateSchema(MemoryDbSchema schema)
    {
        schema.Validate(vectorSizeRequired: true);

        foreach (var f in schema.Fields.Where(x => x.Type == MemoryDbField.FieldType.Vector))
        {
            if (f.VectorMetric is not (MemoryDbField.VectorMetricType.Cosine or MemoryDbField.VectorMetricType.Euclidean or MemoryDbField.VectorMetricType.DotProduct))
            {
                throw new AzureAISearchMemoryException($"Vector metric '{f.VectorMetric:G}' not supported");
            }
        }
    }

    /// <summary>
    /// Options used by the Azure AI Search client, e.g. User Agent and Auth audience
    /// </summary>
    private static SearchClientOptions GetClientOptions(AzureAISearchConfig config)
    {
        var options = new SearchClientOptions
        {
            Diagnostics =
            {
                IsTelemetryEnabled = Telemetry.IsTelemetryEnabled,
                ApplicationId = Telemetry.HttpUserAgent,
            }
        };

        // Custom audience for sovereign clouds.
        // See https://github.com/Azure/azure-sdk-for-net/blob/main/sdk/search/Azure.Search.Documents/src/SearchAudience.cs
        if (config.Auth == AzureAISearchConfig.AuthTypes.AzureIdentity && !string.IsNullOrWhiteSpace(config.AzureIdentityAudience))
        {
            options.Audience = new SearchAudience(config.AzureIdentityAudience);
        }

        return options;
    }

    /// <summary>
    /// Normalize index name to match Azure AI Search rules.
    /// The method doesn't handle all the error scenarios, leaving it to the service
    /// to throw an error for edge cases not handled locally.
    /// </summary>
    /// <param name="index">Value to normalize</param>
    /// <returns>Normalized name</returns>
    private string NormalizeIndexName(string index)
    {
        ArgumentNullExceptionEx.ThrowIfNullOrWhiteSpace(index, nameof(index), "The index name is empty");

        if (index.Length > 128)
        {
            throw new AzureAISearchMemoryException("The index name (prefix included) is too long, it cannot exceed 128 chars.");
        }

        index = index.ToLowerInvariant();

        index = s_replaceIndexNameCharsRegex.Replace(index.Trim(), "-");

        // Name cannot start with a dash
        if (index.StartsWith('-')) { index = $"z{index}"; }

        // Name cannot end with a dash
        if (index.EndsWith('-')) { index = $"{index}z"; }

        return index;
    }

    private SearchIndex PrepareIndexSchema(string index, MemoryDbSchema schema)
    {
        ValidateSchema(schema);

        index = this.NormalizeIndexName(index);

        const string VectorSearchProfileName = "KMDefaultProfile";
        const string VectorSearchConfigName = "KMDefaultAlgorithm";

        var indexSchema = new SearchIndex(index)
        {
            Fields = [],
            VectorSearch = new VectorSearch
            {
                Profiles =
                {
                    new VectorSearchProfile(VectorSearchProfileName, VectorSearchConfigName)
                },
                Algorithms =
                {
                    new HnswAlgorithmConfiguration(VectorSearchConfigName)
                    {
                        Parameters = new HnswParameters
                        {
                            Metric = VectorSearchAlgorithmMetric.Cosine
                        }
                    }
                }
            }
        };

        /* Field attributes: see https://learn.microsoft.com/en-us/azure/search/search-what-is-an-index
         * - searchable: Full-text searchable, subject to lexical analysis such as word-breaking during indexing.
         * - filterable: Filterable fields of type Edm.String or Collection(Edm.String) don't undergo word-breaking.
         * - facetable: Used for counting. Fields of type Edm.String that are filterable, "sortable", or "facetable" can be at most 32kb. */
        VectorSearchField? vectorField = null;
        foreach (var field in schema.Fields)
        {
            switch (field.Type)
            {
                case MemoryDbField.FieldType.Unknown:
                default:
                    throw new AzureAISearchMemoryException($"Unsupported field type {field.Type:G}");

                case MemoryDbField.FieldType.Vector:
                    vectorField = new VectorSearchField(field.Name, field.VectorSize, VectorSearchProfileName)
                    {
                        IsHidden = false,
                        IsStored = true
                    };

                    break;
                case MemoryDbField.FieldType.Text:
                    var useBugWorkAround = true;
                    if (useBugWorkAround)
                    {
                        /* August 2023:
                           - bug: Indexes must have a searchable string field
                           - temporary workaround: make the key field searchable

                         Example of unexpected error:
                            Date: Tue, 01 Aug 2023 23:15:59 GMT
                            Status: 400 (Bad Request)
                            ErrorCode: OperationNotAllowed

                            Content:
                            {"error":{"code":"OperationNotAllowed","message":"If a query contains the search option the
                            target index must contain one or more searchable string fields.\r\nParameter name: search",
                            "details":[{"code":"CannotSearchWithoutSearchableFields","message":"If a query contains the
                            search option the target index must contain one or more searchable string fields."}]}}

                            at Azure.Search.Documents.SearchClient.SearchInternal[T](SearchOptions options,
                            String operationName, Boolean async, CancellationToken cancellationToken)
                         */
                        indexSchema.Fields.Add(new SearchField(field.Name, SearchFieldDataType.String)
                        {
                            IsKey = field.IsKey,
                            IsFilterable = field.IsKey || field.IsFilterable, // Filterable keys are recommended for batch operations
                            IsFacetable = false,
                            IsSortable = false,
                            IsSearchable = true,
                        });
                    }
                    else
                    {
                        indexSchema.Fields.Add(new SimpleField(field.Name, SearchFieldDataType.String)
                        {
                            IsKey = field.IsKey,
                            IsFilterable = field.IsKey || field.IsFilterable, // Filterable keys are recommended for batch operations
                            IsFacetable = false,
                            IsSortable = false,
                        });
                    }

                    break;

                case MemoryDbField.FieldType.Integer:
                    indexSchema.Fields.Add(new SimpleField(field.Name, SearchFieldDataType.Int64)
                    {
                        IsKey = field.IsKey,
                        IsFilterable = field.IsKey || field.IsFilterable, // Filterable keys are recommended for batch operations
                        IsFacetable = false,
                        IsSortable = false,
                    });
                    break;

                case MemoryDbField.FieldType.Decimal:
                    indexSchema.Fields.Add(new SimpleField(field.Name, SearchFieldDataType.Double)
                    {
                        IsKey = field.IsKey,
                        IsFilterable = field.IsKey || field.IsFilterable, // Filterable keys are recommended for batch operations
                        IsFacetable = false,
                        IsSortable = false,
                    });
                    break;

                case MemoryDbField.FieldType.Bool:
                    indexSchema.Fields.Add(new SimpleField(field.Name, SearchFieldDataType.Boolean)
                    {
                        IsKey = false,
                        IsFilterable = field.IsFilterable,
                        IsFacetable = false,
                        IsSortable = false,
                    });
                    break;

                case MemoryDbField.FieldType.ListOfStrings:
                    indexSchema.Fields.Add(new SimpleField(field.Name, SearchFieldDataType.Collection(SearchFieldDataType.String))
                    {
                        IsKey = false,
                        IsFilterable = field.IsFilterable,
                        IsFacetable = false,
                        IsSortable = false,
                    });
                    break;
            }
        }

        // Add the vector field as the last element, so Azure Portal shows
        // the other fields before the long list of floating numbers
        indexSchema.Fields.Add(vectorField);

        return indexSchema;
    }

    private AISearchOptions PrepareSearchOptions(
        AISearchOptions? options,
        bool withEmbeddings,
        ICollection<MemoryFilter>? filters = null,
        int limit = 1)
    {
        options ??= new AISearchOptions();

        // Define which fields to fetch
        options.Select.Add(AzureAISearchMemoryRecord.IdField);
        options.Select.Add(AzureAISearchMemoryRecord.TagsField);
        options.Select.Add(AzureAISearchMemoryRecord.PayloadField);

        // Embeddings are fetched only when needed, to reduce latency and cost
        if (withEmbeddings)
        {
            options.Select.Add(AzureAISearchMemoryRecord.VectorField);
        }

        // Remove empty filters
        filters = filters?.Where(f => !f.IsEmpty()).ToList();

        if (filters is { Count: > 0 })
        {
            options.Filter = AzureAISearchFiltering.BuildSearchFilter(filters);
            this._log.LogDebug("Filtering vectors, condition: {0}", options.Filter);
        }

        // See: https://learn.microsoft.com/azure/search/search-query-understand-collection-filters
        // fieldValue = fieldValue.Replace("'", "''", StringComparison.Ordinal);
        // var options = new SearchOptions
        // {
        //     Filter = fieldIsCollection
        //         ? $"{fieldName}/any(s: s eq '{fieldValue}')"
        //         : $"{fieldName} eq '{fieldValue}')",
        //     Size = limit
        // };

        if (limit > 0)
        {
            options.Size = limit;
            this._log.LogDebug("Max results: {0}", limit);
        }

        // Decide whether to use a sticky session for the current request
        if (this._useStickySessions)
        {
            options.SessionId = Guid.NewGuid().ToString("N");
        }

        return options;
    }

    private static double ScoreToCosineSimilarity(double score)
    {
        return 2 - (1 / score);
    }

    private static double CosineSimilarityToScore(double similarity)
    {
        return 1 / (2 - similarity);
    }

    #endregion
}
