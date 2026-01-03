# About Me (English)

> Source: https://www.mostlylucid.net/blog/aboutme

Notification message

About Me (English)

            About Me

                Wednesday, 31 December 2025

            //

                12 minute read

                     Comments

                     Raw

                     Edit

    introduction

    Resume

        Scott Galloway | AI Systems Engineer | CTO | Head of Engineering | Systems Architect | Remote
I'm a technical leader with over 30 years building and scaling engineering teams and products. I've served as CTO, Head of Engineering, and Lead Architect across startups and enterprises - from pre-seed companies building their first MVP to scaling systems serving millions of users.
I spent several years at Microsoft as an ASP.NET Program Manager on the Redmond campus, which gave me deep insights into how the .NET ecosystem works from the inside. Since 2012, I've run mostlylucid limited, working with companies at every stage: founding technical teams, architecting platforms from scratch, rescuing legacy systems, and leading departments through growth phases.

What I Bring
I scale with you. I've bootstrapped startups as the solo technical founder, built and led teams of 20+ engineers, and served as interim CTO for companies raising their Series A. Whether you need someone to architect your MVP, build your first engineering team, or take over as Head of Engineering, I've done it.
I'm hands-on when it matters. I can (and do) write production code, debug gnarly performance issues, and architect complex distributed systems. But I also know when to step back and let the team execute. I've hired, mentored, and managed engineers across multiple continents.
I build teams that last. I created a developer training program that placed 200+ junior developers into the industry (90%+ success rate). I believe in growing talent internally and creating engineering cultures where people do their best work.
My sweet spot: Early to mid-stage startups that need someone who can architect the right solution, build or lead the team to deliver it, and scale both the system and the organization as the company grows. I'm equally comfortable leading a department or rolling up my sleeves as the technical co-founder on a small team.
Tech stack: I've worked in everything from Perl CGI scripts to modern Go microservices, though my primary focus is .NET (ASP.NET Core through .NET 9). I'm language-agnostic and pragmatic about choosing the right tool for the job - whether that's modern frontend frameworks (HTMX, Alpine.js, React, Vue.js), databases (PostgreSQL, SQL Server, MongoDB), or infrastructure (Docker, Kubernetes, Azure). I build in public and contribute back to the dev community through open-source packages and detailed technical writing.
Open Source & Packages
I believe in giving back to the dev community. Here's what I've published:
CLI Tools
Self-contained, portable executables; no runtime install required.

BotDetection - Detects bot / scraper / hacker activity in web traffic using machine learning models. Supports HTTP request analysis, user agent parsing, and IP reputation checks. Provides real-time alerts and customizable rules for blocking suspicious traffic and optional LLM interpretation .

DataSummarizer - Fast, local, privacy-first data profiling with DuckDB + optional LLM narration. Profile any CSV, Excel, Parquet, JSON, SQLite, or log file in seconds. Features include 52K+ rows profiled in ~1 second, privacy-safe PII detection, drift detection, constraint validation, and natural language Q&A with SQL generation.

DocSummarizer - Turn documents or URLs into evidence-grounded summaries where every claim cites its source. Runs entirely on your machine. Supports PDF, DOCX, PPTX, XLSX, Markdown, and HTML. Offers Bert mode (pure ONNX, no LLM, ~1-3 seconds) and BertRag mode (LLM synthesis with citations). Includes an MCP server for AI agent integration.

NuGet Packages

Mostlylucid.MinimalBlog - A minimal, file-based markdown blog for ASP.NET Core. Just point to a folder of markdown files and go. ~500 lines of code total, with memory and output caching built-in, MetaWeblog API support, and a GitHub-inspired dark theme.

Umami.Net - A .NET client for Umami Web Analytics. Privacy-focused analytics made simple.

mostlylucid.Markdig.FetchExtension - Complete solution for fetching and caching remote markdown content with multiple storage backends and a stale-while-revalidate pattern.

mostlylucid.mockllmapi - Lightweight ASP.NET Core middleware for generating realistic mock API responses using local LLMs (via Ollama). Add intelligent mock endpoints to any project with just 2 lines of code.

mostlylucid.pagingtaghelper - HTMX-enabled ASP.NET Core Tag Helpers for paging.

MostlyLucid.EufySecurity - A highly experimental .NET client library for controlling Eufy Security devices. It works, but use at your own risk.

NPM Packages

@mostlylucid/mermaid-enhancements - Enhances Mermaid diagrams with export, panning, zoom, expanding lightbox, and theme switching.

All of these came out of real needs in projects I was working on. When I hit a problem that doesn't have a good solution, I build one and share it.
Additional Projects
These are experimental projects and demos from the mostlylucidweb repository:

TinyLLM - Windows WPF app for local AI chat with RAG memory. Supports Ollama and direct GGUF model loading.

Chat.Server - SignalR hub for real-time chat between website visitors and administrators.

Chat.Widget - Embeddable JavaScript chat widget for any website.

SemanticSearch - CPU-friendly semantic search using ONNX embeddings and Qdrant vector database.

BlogLLM - RAG knowledge base builder for markdown documents.

SemanticGallery.Demo - Image gallery with semantic search capabilities.

SentimentAnalysis - Local sentiment analysis using ONNX models.

Workflow.Engine - Simple workflow execution engine.

Background & Experience
Microsoft (2005-2009): Started as an Application Developer Consultant in Reading, UK, focusing on performance analysis and tuning. Later moved to Redmond, WA as a Program Manager II on the ASP.NET team, where I managed release lifecycles and worked on security infrastructure for Project Server.
Dell (2011): Built a custom machine image deployment platform using ASP.NET MVC.
Freelance (2012-Present): I've worked with companies like GBG Plc (Loqate), where I built global search products using .NET 6 microservices and Kubernetes. At Seamcor Ltd, I directed teams to rearchitect ASP.NET Core systems using Docker Compose and OpenSearch. Most recently at ZenChef Limited, I led the transition from legacy Azure systems to .NET 8, substantially reducing hosting costs through profiling and optimization.
I've also built and run a developer training program that's helped over 400 junior developers break into the industry (90% placement rate). Teaching and mentoring is something I genuinely love.
This Site
I FORMERLY ran the website www.mostlylucid.co.uk where I published popular  ASP.NET articles for 10 years before joining the ASP.NET Team at Microsoft in Redmond. After leaving there I closed the site (it was a measured metric at Microsoft how often I posted and it sucked the fun out of it) then opened (lasst year)...
mostlylucid.net is my crazy lab - a living codebase where I experiment with whatever catches my interest in web development. The entire platform is open source on GitHub, and it's become a breeding ground for the NuGet and NPM packages I've published.
The theme: See problem, find solution, implement it, then extract it into a reusable package.
The infrastructure itself is a case study in efficient server operations:

Docker Compose orchestration for the entire stack (app, database, analytics, translation services, monitoring)
Full CI/CD pipeline with GitHub Actions, automated testing, and deployment
Comprehensive observability (Serilog + Seq, Prometheus, Grafana) - because you can't fix what you can't see
Self-hosted everything - database, analytics (Umami), translation (EasyNMT), reverse proxy (Caddy)

The platform features that spawned packages:

Dual-mode blog system (file-based markdown or PostgreSQL) - led to the Markdig.FetchExtension package and MinimalBlog package
Automated translation system - complete Python / Pytorch / FastAPI rewrite of EasyNMT supporting 100+ languages with automatic fallback between neural machine translation model families. Production-ready with intelligent caching, GPU optimisation, and robust error handling. Translates the entire blog to 12+ languages automatically. This is exactly my wheelhouse: novel technology solving a real problem (abandoned EasyNMT project) with an amazing solution that handles messy real-world input at scale.
Full-text search using PostgreSQL tsvector/GIN indexes
Semantic search with ONNX embeddings and Qdrant vector database - CPU-friendly, no GPU required
Real-time chat system with SignalR hub and embeddable widget
Interactive Mermaid diagrams with pan/zoom/export - became the @mostlylucid/mermaid-enhancements npm package
HTMX-powered paging - extracted into the pagingtaghelper NuGet package
Umami analytics integration - became the Umami.Net package
LLM-powered mock APIs for development - became the mockllmapi package
Document and data summarisation tools - became the DocSummarizer and DataSummarizer CLI tools

It's messy, experimental, and constantly evolving. That's the point.
Contact
Email: [email protected]
Blog: https://www.mostlylucid.net
GitHub: https://github.com/scottgal
LinkedIn: Scott Galloway
Looking for consulting help or a contract role? I'm always open to interesting projects. Drop me a line.

The Details (If You Need Them)
Tech Stack
Languages: From Perl CGI to Go microservices - C#/.NET (my primary focus), Python, Java, Node.js, JavaScript/TypeScript, PHP, C++. I'm language-agnostic and choose based on the problem, not the hype.
Backend: ASP.NET Core through .NET 10, Node.js, Python APIs, Go services
Frontend: HTMX, Alpine.js, Vue.js, React, Angular, Blazor, jQuery (whatever fits the use case)
CSS: Tailwind CSS, Bootstrap, DaisyUI
Databases: PostgreSQL, SQL Server, MySQL, SQLite, MongoDB, RavenDB, Cosmos, OpenSearch
DevOps: Docker, Kubernetes, GitHub Actions, Azure DevOps, CI/CD pipelines, Infrastructure-as-Code
Cloud: Azure (though I increasingly prefer self-hosted solutions for cost and control)
AI/ML: Ollama, ONNX Runtime, Qdrant, DuckDB, EasyNMT, ML.NET, scikit-learn, embeddings, vector search, RAG pipelines, MCP servers, Azure OpenAI
Career Highlights
Microsoft Corporation (2005-2009): Started as Application Developer Consultant II in Reading, UK, focusing on performance analysis and enterprise consulting for FTSE 100 clients. Promoted to Program Manager II in Redmond, WA, where I managed ASP.NET release lifecycles and drove security infrastructure for Project Server.
mostlylucid limited (2012-Present): Through my consultancy, I've delivered dozens of solutions across fintech, healthcare, ecommerce, and government sectors in both contract and full-time roles. Notable engagements include:

Lead Architect/Developer for a major Asian airline's booking system (Knockout & ASP.NET MVC)
R&D Developer for insurance company telemetry tracking (low-level socket systems on Azure)
Research Systems Architect on UK Government National Security distributed computing project
Technical consultant for 8 UK and international charities

Recent Roles (Contract & Full-Time):
ZenChef Limited (Oct 2024 - Present): Lead the transition of a large legacy Azure-based distributed system to .NET 8, substantially reducing hosting costs through profiling and optimization. Continue to support the massive legacy system.
Very Jane Ltd (Apr 2024 - Aug 2024): Backend integration for a large e-commerce application, implementing Stripe Connect and Hyperwallet payment systems.
Seamcor Ltd (Dec 2022 - Apr 2024): Rearchitected an ASP.NET Core system as Head of Engineering, implementing Docker Compose and OpenSearch for enhanced reporting.
GBG Plc (Loqate) (Aug 2022 - Nov 2022) [Full-Time]: Built a global search product using .NET 6 microservices and Kubernetes for high-throughput, large-scale data processing.
H3Space Ltd (Jun 2021 - Sep 2021) [Full-Time]: Designed backend cloud architecture for Unity-based SaaS platform with React and GraphQL. Hired and led a global dev team to MVP delivery within 90 days.
Dell (Jan 2011 - Jul 2011) [Full-Time]: Architected an internal machine provisioning platform with ASP.NET MVC and SQL Server, reducing setup time by 70%.
Education
University of Stirling - BSc (Hons) Psychology
Stirling, Scotland, UK
(Yes, psychology. It's actually been incredibly useful in understanding user behaviour and leading teams.)

Why Work With Me?
From Perl CGI scripts to cloud platforms, I've spent over two decades building and running software that holds up under pressure. I've worked across ASP, .NET, and today's Core/Azure/Kubernetes stack, leading teams and reworking legacy systems along the way.
I add AI when it's useful, not because it's trendy. I've built ML-based recommendation systems, lead generation tools using Azure OpenAI and vector embeddings, and RAG pipelines. But I also know when simpler solutions are better.
I make things work properly and make them last. I've migrated legacy monoliths to cloud-native architectures, cutting infrastructure costs by 50% and improving deployment times by 80%. I've reduced runtime errors by >90% in multiple systems through careful profiling and optimization.
I've led developer bootcamps with 200+ graduates and a 90%+ placement rate. Teaching and mentorship aren't side projects for me - they're core to how I work. I believe in building teams that outlast any single project.
I deliver. Whether it's architecting a global search product on Kubernetes, integrating payment systems for e-commerce platforms, or modernizing legacy systems under tight deadlines, I've done it across fintech, healthcare, government, and SaaS.
The thread through it all is simple: make things work properly and make them last.
If you're looking for someone who can architect, build, lead, and ship - someone who's been doing this since before "full-stack" was a term - let's talk.

             Add Comment

                Tom
                •
                one year ago

                Approved

            Hello. Are you going to make the length of your pages on this blog more than 5 posts long? also, this comment box is behaving oddly on my iphone. None of the writing features like spelling corrections, double tap for period, etc are working.

                Scott Galloway
                •
                one year ago

                Approved

            Hi, yes working on a page size control now. Stay tuned. Unfortunately I don't have an iPhone to test the editor.

    © 2025 Scott Galloway — 
    Unlicense — 
    All content and source code on this site is free to use, copy, modify, and sell.
