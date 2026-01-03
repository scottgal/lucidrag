using FluentAssertions;
using LucidRAG.Services;

namespace LucidRAG.Tests.Services;

public class RobotsTxtTests
{
    [Fact]
    public void AllowAll_AllowsAllPaths()
    {
        // Arrange
        var robots = RobotsTxt.AllowAll;

        // Act & Assert
        robots.IsAllowed("/", "LucidRAG").Should().BeTrue();
        robots.IsAllowed("/any/path", "LucidRAG").Should().BeTrue();
        robots.IsAllowed("/admin/secret", "LucidRAG").Should().BeTrue();
    }

    [Fact]
    public void Parse_EmptyContent_AllowsAllPaths()
    {
        // Arrange
        var robots = RobotsTxt.Parse("");

        // Act & Assert
        robots.IsAllowed("/anything", "LucidRAG").Should().BeTrue();
    }

    [Fact]
    public void Parse_WildcardUserAgent_AppliesDisallow()
    {
        // Arrange
        var content = """
            User-agent: *
            Disallow: /admin
            Disallow: /private/
            """;
        var robots = RobotsTxt.Parse(content);

        // Act & Assert
        robots.IsAllowed("/", "LucidRAG").Should().BeTrue();
        robots.IsAllowed("/public", "LucidRAG").Should().BeTrue();
        robots.IsAllowed("/admin", "LucidRAG").Should().BeFalse();
        robots.IsAllowed("/admin/users", "LucidRAG").Should().BeFalse();
        robots.IsAllowed("/private/", "LucidRAG").Should().BeFalse();
        robots.IsAllowed("/private/docs", "LucidRAG").Should().BeFalse();
    }

    [Fact]
    public void Parse_SpecificUserAgent_AppliesToLucidRAG()
    {
        // Arrange
        var content = """
            User-agent: LucidRAG
            Disallow: /secret

            User-agent: Googlebot
            Disallow: /google-only
            """;
        var robots = RobotsTxt.Parse(content);

        // Act & Assert
        robots.IsAllowed("/secret", "LucidRAG").Should().BeFalse();
        // Note: Our parser is simple and doesn't track per-user-agent rules strictly
        // It applies rules for * and LucidRAG
        robots.IsAllowed("/public", "LucidRAG").Should().BeTrue();
    }

    [Fact]
    public void Parse_AllowTakesPrecedenceOverDisallow()
    {
        // Arrange
        var content = """
            User-agent: *
            Disallow: /docs/
            Allow: /docs/public
            """;
        var robots = RobotsTxt.Parse(content);

        // Act & Assert
        robots.IsAllowed("/docs/public", "LucidRAG").Should().BeTrue();
        robots.IsAllowed("/docs/private", "LucidRAG").Should().BeFalse();
    }

    [Fact]
    public void Parse_WildcardPattern_MatchesPrefix()
    {
        // Arrange
        var content = """
            User-agent: *
            Disallow: /api/*
            """;
        var robots = RobotsTxt.Parse(content);

        // Act & Assert
        robots.IsAllowed("/api/v1/users", "LucidRAG").Should().BeFalse();
        robots.IsAllowed("/api/", "LucidRAG").Should().BeFalse();
        robots.IsAllowed("/api", "LucidRAG").Should().BeTrue(); // Exact match without trailing
    }

    [Fact]
    public void Parse_CommentsAreIgnored()
    {
        // Arrange
        var content = """
            # This is a comment
            User-agent: *
            # Another comment
            Disallow: /blocked
            """;
        var robots = RobotsTxt.Parse(content);

        // Act & Assert
        robots.IsAllowed("/blocked", "LucidRAG").Should().BeFalse();
        robots.IsAllowed("/allowed", "LucidRAG").Should().BeTrue();
    }

    [Fact]
    public void Parse_EmptyDisallow_AllowsAll()
    {
        // Arrange - empty Disallow means allow all
        var content = """
            User-agent: *
            Disallow:
            """;
        var robots = RobotsTxt.Parse(content);

        // Act & Assert
        robots.IsAllowed("/anything", "LucidRAG").Should().BeTrue();
    }

    [Fact]
    public void Parse_CaseInsensitiveUserAgent()
    {
        // Arrange
        var content = """
            User-agent: lucidrag
            Disallow: /secret
            """;
        var robots = RobotsTxt.Parse(content);

        // Act & Assert
        robots.IsAllowed("/secret", "LucidRAG").Should().BeFalse();
    }

    [Fact]
    public void Parse_MalformedLines_AreSkipped()
    {
        // Arrange
        var content = """
            User-agent: *
            This is not a valid directive
            NoColon
            Disallow: /blocked
            """;
        var robots = RobotsTxt.Parse(content);

        // Act & Assert
        robots.IsAllowed("/blocked", "LucidRAG").Should().BeFalse();
        robots.IsAllowed("/other", "LucidRAG").Should().BeTrue();
    }

    [Fact]
    public void Parse_RealWorldExample_WordPress()
    {
        // Arrange - typical WordPress robots.txt
        var content = """
            User-agent: *
            Disallow: /wp-admin/
            Allow: /wp-admin/admin-ajax.php
            Disallow: /wp-includes/
            Disallow: /xmlrpc.php

            Sitemap: https://example.com/sitemap.xml
            """;
        var robots = RobotsTxt.Parse(content);

        // Act & Assert
        robots.IsAllowed("/", "LucidRAG").Should().BeTrue();
        robots.IsAllowed("/blog/post", "LucidRAG").Should().BeTrue();
        robots.IsAllowed("/wp-admin/", "LucidRAG").Should().BeFalse();
        robots.IsAllowed("/wp-admin/admin-ajax.php", "LucidRAG").Should().BeTrue();
        robots.IsAllowed("/wp-includes/js/script.js", "LucidRAG").Should().BeFalse();
        robots.IsAllowed("/xmlrpc.php", "LucidRAG").Should().BeFalse();
    }

    [Fact]
    public void Parse_DisallowRoot_BlocksEverything()
    {
        // Arrange
        var content = """
            User-agent: *
            Disallow: /
            """;
        var robots = RobotsTxt.Parse(content);

        // Act & Assert
        robots.IsAllowed("/", "LucidRAG").Should().BeFalse();
        robots.IsAllowed("/anything", "LucidRAG").Should().BeFalse();
    }
}
