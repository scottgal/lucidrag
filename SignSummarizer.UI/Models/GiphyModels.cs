using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SignSummarizer.UI.Models;

public sealed class GiphyResponse
{
    [JsonPropertyName("data")]
    public List<GiphyGif> Data { get; set; } = new();
    
    [JsonPropertyName("pagination")]
    public GiphyPagination? Pagination { get; set; }
    
    [JsonPropertyName("meta")]
    public GiphyMeta? Meta { get; set; }
}

public sealed class GiphyGif
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;
    
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;
    
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;
    
    [JsonPropertyName("slug")]
    public string Slug { get; set; } = string.Empty;
    
    [JsonPropertyName("bitly_url")]
    public string BitlyUrl { get; set; } = string.Empty;
    
    [JsonPropertyName("bitly_gif_url")]
    public string BitlyGifUrl { get; set; } = string.Empty;
    
    [JsonPropertyName("embed_url")]
    public string EmbedUrl { get; set; } = string.Empty;
    
    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;
    
    [JsonPropertyName("source")]
    public string? Source { get; set; }
    
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;
    
    [JsonPropertyName("rating")]
    public string Rating { get; set; } = string.Empty;
    
    [JsonPropertyName("content_url")]
    public string? ContentUrl { get; set; }
    
    [JsonPropertyName("source_tld")]
    public string? SourceTld { get; set; }
    
    [JsonPropertyName("source_post_url")]
    public string? SourcePostUrl { get; set; }
    
    [JsonPropertyName("is_indexable")]
    public int IsIndexable { get; set; }
    
    [JsonPropertyName("is_sticker")]
    public int IsSticker { get; set; }
    
    [JsonPropertyName("import_datetime")]
    public DateTime ImportDateTime { get; set; }
    
    [JsonPropertyName("trending_datetime")]
    public string? TrendingDatetime { get; set; }
    
    [JsonPropertyName("images")]
    public GiphyImages Images { get; set; } = new();
    
    [JsonPropertyName("user")]
    public GiphyUser? User { get; set; }
    
    [JsonPropertyName("analytics_response_payload")]
    public string? AnalyticsResponsePayload { get; set; }
    
    [JsonPropertyName("analytics")]
    public GiphyAnalytics? Analytics { get; set; }
}

public sealed class GiphyImages
{
    [JsonPropertyName("original")]
    public GiphyImage Original { get; set; } = new();
    
    [JsonPropertyName("downsized")]
    public GiphyImage Downsized { get; set; } = new();
    
    [JsonPropertyName("fixed_height")]
    public GiphyImage FixedHeight { get; set; } = new();
    
    [JsonPropertyName("fixed_height_downsampled")]
    public GiphyImage FixedHeightDownsampled { get; set; } = new();
    
    [JsonPropertyName("fixed_width")]
    public GiphyImage FixedWidth { get; set; } = new();
    
    [JsonPropertyName("fixed_width_downsampled")]
    public GiphyImage FixedWidthDownsampled { get; set; } = new();
    
    [JsonPropertyName("fixed_height_small")]
    public GiphyImage FixedHeightSmall { get; set; } = new();
    
    [JsonPropertyName("fixed_width_small")]
    public GiphyImage FixedWidthSmall { get; set; } = new();
    
    [JsonPropertyName("original_still")]
    public GiphyImage OriginalStill { get; set; } = new();
    
    [JsonPropertyName("downsized_still")]
    public GiphyImage DownsizedStill { get; set; } = new();
    
    [JsonPropertyName("fixed_height_still")]
    public GiphyImage FixedHeightStill { get; set; } = new();
    
    [JsonPropertyName("fixed_width_still")]
    public GiphyImage FixedWidthStill { get; set; } = new();
}

public sealed class GiphyImage
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;
    
    [JsonPropertyName("width")]
    public string Width { get; set; } = string.Empty;
    
    [JsonPropertyName("height")]
    public string Height { get; set; } = string.Empty;
    
    [JsonPropertyName("size")]
    public string Size { get; set; } = string.Empty;
    
    [JsonPropertyName("mp4")]
    public string? Mp4 { get; set; }
    
    [JsonPropertyName("mp4_size")]
    public string? Mp4Size { get; set; }
    
    [JsonPropertyName("webp")]
    public string? Webp { get; set; }
    
    [JsonPropertyName("webp_size")]
    public string? WebpSize { get; set; }
    
    [JsonPropertyName("frames")]
    public string? Frames { get; set; }
}

public sealed class GiphyUser
{
    [JsonPropertyName("avatar_url")]
    public string AvatarUrl { get; set; } = string.Empty;
    
    [JsonPropertyName("banner_url")]
    public string BannerUrl { get; set; } = string.Empty;
    
    [JsonPropertyName("profile_url")]
    public string ProfileUrl { get; set; } = string.Empty;
    
    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;
    
    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = string.Empty;
    
    [JsonPropertyName("description")]
    public string? Description { get; set; }
    
    [JsonPropertyName("instagram_url")]
    public string? InstagramUrl { get; set; }
    
    [JsonPropertyName("website_url")]
    public string? WebsiteUrl { get; set; }
    
    [JsonPropertyName("is_verified")]
    public bool IsVerified { get; set; }
}

public sealed class GiphyAnalytics
{
    [JsonPropertyName("onload")]
    public Dictionary<string, string>? Onload { get; set; }
    
    [JsonPropertyName("onclick")]
    public Dictionary<string, string>? Onclick { get; set; }
    
    [JsonPropertyName("onsent")]
    public Dictionary<string, string>? Onsent { get; set; }
}

public sealed class GiphyPagination
{
    [JsonPropertyName("total_count")]
    public int TotalCount { get; set; }
    
    [JsonPropertyName("count")]
    public int Count { get; set; }
    
    [JsonPropertyName("offset")]
    public int Offset { get; set; }
}

public sealed class GiphyMeta
{
    [JsonPropertyName("status")]
    public int Status { get; set; }
    
    [JsonPropertyName("msg")]
    public string? Msg { get; set; }
    
    [JsonPropertyName("response_id")]
    public string? ResponseId { get; set; }
}