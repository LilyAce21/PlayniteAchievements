using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Services.Estimation
{
    /// <summary>One web result: a snippet, and the page text when it was asked for.</summary>
    public sealed class SearchHit
    {
        public string Title { get; set; }
        public string Url { get; set; }

        /// <summary>The part of the page the search engine judged relevant to the query.</summary>
        public string Snippet { get; set; }

        /// <summary>The page's own text, when the request asked for it. May be very long.</summary>
        public string RawContent { get; set; }
    }

    public enum SearchFailureKind
    {
        None = 0,

        /// <summary>The service cannot be used now and retrying will not help: no key, a rejected key, or the plan's credits are used up.</summary>
        Unavailable,

        /// <summary>This search failed (network, rate limit, server trouble). Others may still work.</summary>
        Transient
    }

    public sealed class SearchRequest
    {
        public string Query { get; set; }
        public int MaxResults { get; set; } = 5;

        /// <summary>Ask for each page's text as well as the snippet (needed to find every achievement in a guide).</summary>
        public bool IncludeRawContent { get; set; }
    }

    public sealed class SearchResponse
    {
        public bool Success { get; set; }
        public List<SearchHit> Hits { get; } = new List<SearchHit>();

        /// <summary>What the search cost, in the provider's credits.</summary>
        public int CreditsUsed { get; set; }

        public SearchFailureKind Failure { get; set; }

        /// <summary>A short, safe explanation for the user. Never contains the API key.</summary>
        public string Error { get; set; }

        public static SearchResponse Fail(SearchFailureKind kind, string error) =>
            new SearchResponse { Failure = kind, Error = error };
    }

    /// <summary>A web search service. The plugin searches itself and hands the findings to the model as notes.</summary>
    public interface ISearchProvider
    {
        Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken);
    }
}
