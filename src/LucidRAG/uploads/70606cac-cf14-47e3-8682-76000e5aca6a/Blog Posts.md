# Blog Posts

> Source: https://www.mostlylucid.net/blog

Page size:

                10

                25

                50

                75

                100

                125

                150

                200

                250

                500

                760

                            1

                            2

                            3

                            4

                            5

                            ..

                            Next ›

                            »

                Page 1 of 38 (Total items: 760)

            DocSummarizer Part 5 - lucidRAG: Multi-Document RAG Web Application

    AI

    C#

    DuckDB

    GraphRAG

    HTMX

    LLM

    RAG

    Semantic Search

    This is Part 5 of the DocSummarizer series, and it's also the culmination of the GraphRAG series and Semantic Search series. We're combining everything into a deployable web application.
🚨🚨 PREVIEW...

            Thursday, 01 January 2026 18:00

        //

            8 minute read

            About Me

    introduction

    Resume

    Scott Galloway | AI Systems Engineer | CTO | Head of Engineering | Systems Architect | Remote
I'm a technical leader with over 30 years building and scaling engineering teams and products. I've served...

            Wednesday, 31 December 2025 22:30

        //

            12 minute read

            Zero PII Customer Intelligence - Part 1: The Philosophy

    C#

    Privacy

    Product

    Segmentation

    What This Series Builds
This series builds a working ecommerce system that proves you can have sophisticated customer intelligence without storing personal information.
What We Build How It Works...

            Wednesday, 31 December 2025 20:00

        //

            14 minute read

            Zero PII Customer Intelligence - Part 1.1: Generating Sample Data (and Images) Locally

    C#

    ComfyUI

    LLM

    Privacy

    Product

    In Part 1 we covered the philosophy: transparent segmentation without PII. Part 2 covers session profiles, signals, and segment definitions.
But first: how do you validate any of this without ever...

            Wednesday, 31 December 2025 20:00

        //

            9 minute read

            Zero PII Customer Intelligence - Part 2: Profiles, Signals & Segments

    C#

    Privacy

    Product

    Segmentation

    In Part 1 we covered the philosophy and series overview. In Part 1.1 we built the sample data generator.
Now let's build the core system. This part focuses on:
Zero-PII profile architecture -...

            Wednesday, 31 December 2025 20:00

        //

            20 minute read

            "How do you work on all these projects and write these articles, is it just AI doing it all?"

    Q&A

    Short

    Yes and no, I've ALWAYS been able to have multiple ideas in my head at the same time and let them coalesce until it's something I can build (I gave up all the 'social life' parts so it's not ALL...

            Tuesday, 30 December 2025 21:35

        //

            1 minute read

            DocSummarizer Part 4 - Building RAG Pipelines

    AI

    C#

    Embeddings

    LLM

    ONNX

    RAG

    Semantic Search

    NuGet
npm
.NET
Node.js
This is Part 4 of the DocSummarizer series. See Part 1 for the architecture, Part 2 for the CLI tool, or Part 3 for the deep dive on embeddings.
The hard part of RAG isn't the...

            Tuesday, 30 December 2025 17:00

        //

            16 minute read

            Why You Probably Shouldn't Use Microservices (Yet)

    Architecture

    Microservices

    Opinion

    Microservices have become the default "serious system" architecture.
If you want to sound mature, you talk about service meshes, event buses, distributed tracing, and "independent deployability". If...

            Monday, 29 December 2025 20:00

        //

            23 minute read

            HTTP Over the Decades: A Story of Physics, Latency, and Grudging Adaptation

    HTTP

    Networking

    Opinion

    Web Development

    HTTP didn't evolve. It was forced to change by physics, latency, and misuse.
Every version exists because the previous one hit a hard constraint. If you understand those constraints, you understand...

            Sunday, 28 December 2025 18:00

        //

            23 minute read

            No, Small Models Are Not the "Budget Option"

    AI

    LLM

    Ollama

    ONNX

    Opinion

    Small and local LLMs are often framed as the cheap alternative to frontier models. That framing is wrong. They are not a degraded version of the same thing. They are a different architectural choice,...

            Sunday, 28 December 2025 16:00

        //

            7 minute read

            GraphRAG Part 2: Minimum Viable GraphRAG (No Per-Chunk LLM Calls)

    ASP.NET

    DuckDB

    GraphRAG

    Knowledge Graphs

    Machine Learning

    Vector Search

    In Part 1, we explored why GraphRAG matters. Now let's build a minimum viable GraphRAG that works without per-chunk LLM calls - pragmatic, offline-first, and cheap enough to run on a laptop:
DuckDB...

            Saturday, 27 December 2025 14:00

        //

            11 minute read

            GraphRAG: Why Vector Search Breaks Down at the Corpus Level

    ASP.NET

    Machine Learning

    ONNX

    Qdrant

    RAG

    Semantic Search

    Vector Search

    Your RAG system is great at "needle" questions: retrieve a few relevant chunks and synthesise an answer. It struggles with two common query types:
Sensemaking: "What are the main themes across this...

            Friday, 26 December 2025 12:00

        //

            21 minute read

            Probability Is Not a System: The Ten Commandments of LLM Use

    AI

    LLM

    Opinion

    I keep seeing the same failure mode in "AI-powered" systems: LLMs are being asked to do jobs we already solved decades ago - badly, probabilistically, and without guarantees.
This isn't cutting edge....

            Thursday, 25 December 2025 12:40

        //

            14 minute read

            DataSummarizer: Fast Local Data Profiling

    C#

    Data Analysis

    DuckDB

    LLM

    Most “chat with your data” systems make the same mistake: they treat an LLM as if it were a database.
They shove rows into context, embed chunks, or pick “representative samples” and hope the model...

            Monday, 22 December 2025 18:30

        //

            6 minute read

            DocSummarizer Part 3 - Advanced Concepts: The "I Went Too Far 🤦" Deep Dive

    .NET

    AI

    BERT

    C#

    Embeddings

    LLM

    ONNX

    RAG

    This is Part 3 of the DocSummarizer series:
Part 1: Building a Document Summarizer with RAG - The architecture and why the pipeline approach beats naive LLM calls
Part 2: Using the Tool - Quick-start...

            Sunday, 21 December 2025 12:00

        //

            29 minute read

            DocSummarizer Part 2 - Using the Tool

    AI

    C#

    Docling

    LLM

    Ollama

    Qdrant

    RAG

    Tools

    GitHub release
.NET
Version
This is Part 2 of the DocSummarizer series. See Part 1 for the architecture and patterns, or Part 3 for the deep technical dive into embeddings and retrieval.
Turn...

            Sunday, 21 December 2025 11:00

        //

            21 minute read

            Stop Shoving Documents Into LLMs: Build a Local Summarizer with Docling + RAG

    AI

    C#

    Docling

    LLM

    Ollama

    Qdrant

    RAG

    Here's the mistake everyone makes with document summarization: they extract the text and send as much as fits to an LLM. The LLM does its best with whatever landed in context, structure gets...

            Sunday, 21 December 2025 10:00

        //

            13 minute read

            Fetching and Analysing Web Content with LLMs in C#

    Agents

    AI

    C#

    LLM

    Systems Design

    📌 Note: This article teaches the fundamentals of web content extraction with LLMs using the simplest possible approach. For production use cases (web summarization, document analysis, agent tools),...

            Friday, 19 December 2025 10:00

        //

            16 minute read

            How to Analyse Large CSV Files with Local LLMs in C#

    AI

    C#

    Data Analysis

    DuckDB

    LLM

    Ollama

    Series: Local LLMs for Data - Part 1 of 2
Here's the mistake everyone makes: they try to feed their CSV into an LLM. Don't. LLMs should generate queries, not consume data.
You've got a 500MB CSV file...

            Thursday, 18 December 2025 10:00

        //

            18 minute read

            Why I Don't Use LangChain (and What I Do Instead)

    Agents

    AI

    Architecture

    C#

    LLM

    Systems Design

    I'm a .NET developer. When I started building LLM-powered systems, everyone pointed me toward LangChain. "It's the standard," they said. "All the examples use it." And they were right - if you're in...

            Thursday, 18 December 2025 10:00

        //

            14 minute read

        Page size:

                10

                25

                50

                75

                100

                125

                150

                200

                250

                500

                760

                            1

                            2

                            3

                            4

                            5

                            ..

                            Next ›

                            »

                Page 1 of 38 (Total items: 760)
