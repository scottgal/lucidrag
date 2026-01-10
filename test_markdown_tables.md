# Test Markdown Tables for Converter

This file contains various markdown tables to test the DataSummarizer markdown converter.

## Table 1: Simple Sales Data

| Product    | Quantity | Price  | Revenue  |
|------------|----------|--------|----------|
| Widget A   | 100      | $10.99 | $1,099   |
| Widget B   | 250      | $5.99  | $1,497.50|
| Widget C   | 75       | $25.00 | $1,875   |
| Gadget X   | 150      | $15.50 | $2,325   |
| Gadget Y   | 200      | $8.75  | $1,750   |

## Table 2: Employee Directory

| Name          | Department   | Email                    | Phone         | Hire Date  |
|---------------|--------------|--------------------------|---------------|------------|
| Alice Smith   | Engineering  | alice@example.com        | 555-0101      | 2020-01-15 |
| Bob Johnson   | Sales        | bob@example.com          | 555-0102      | 2019-06-20 |
| Carol White   | Marketing    | carol@example.com        | 555-0103      | 2021-03-10 |
| David Brown   | Engineering  | david@example.com        | 555-0104      | 2018-11-05 |
| Eve Davis     | HR           | eve@example.com          | 555-0105      | 2022-02-14 |

## Table 3: With Special Characters & Formatting

| Item          | Description                    | Tags            | Notes                        |
|---------------|--------------------------------|-----------------|------------------------------|
| **Item A**    | Has *italic* and **bold**      | `tech`, `new`   | "Best seller"                |
| Item B        | Contains, commas, everywhere   | finance         | Price: $10.99                |
| Item C        | Has [link](http://example.com) | web             | Visit: http://example.com    |
| Item D        | Multi-word description         | a, b, c         | 100% satisfaction            |

## Table 4: Aligned Columns (Testing :---, ---:, :---:)

| Left-aligned | Center-aligned | Right-aligned | Number  |
|:-------------|:--------------:|--------------:|--------:|
| Apple        | Red            | Fruit         | 3.50    |
| Banana       | Yellow         | Fruit         | 2.25    |
| Carrot       | Orange         | Vegetable     | 1.75    |
| Broccoli     | Green          | Vegetable     | 2.99    |

## Table 5: Wide Table (Many Columns)

| ID | Name    | Age | City    | State | Zip   | Country | Phone      | Email              | Status  |
|----|---------|-----|---------|-------|-------|---------|------------|--------------------|---------|
| 1  | Alice   | 30  | NYC     | NY    | 10001 | USA     | 5550101    | alice@test.com     | Active  |
| 2  | Bob     | 25  | LA      | CA    | 90001 | USA     | 5550102    | bob@test.com       | Active  |
| 3  | Carol   | 35  | Chicago | IL    | 60601 | USA     | 5550103    | carol@test.com     | Pending |
| 4  | David   | 28  | Houston | TX    | 77001 | USA     | 5550104    | david@test.com     | Active  |

## Table 6: Empty Cells

| Product | Q1    | Q2    | Q3    | Q4    |
|---------|-------|-------|-------|-------|
| Alpha   | 100   |       | 150   | 200   |
| Beta    |       | 75    | 90    |       |
| Gamma   | 50    | 60    |       | 80    |
| Delta   |       |       | 120   | 140   |

## Table 7: Numeric Data for Statistical Analysis

| Region    | Sales_2022 | Sales_2023 | Growth_% | Target_2024 |
|-----------|------------|------------|----------|-------------|
| North     | 125000     | 145000     | 16.0     | 175000      |
| South     | 98000      | 112000     | 14.3     | 135000      |
| East      | 156000     | 178000     | 14.1     | 210000      |
| West      | 203000     | 245000     | 20.7     | 295000      |
| Central   | 87000      | 95000      | 9.2      | 110000      |

Some regular text between tables...

## Table 8: Small Two-Column Table

| Category   | Count |
|------------|-------|
| Red        | 45    |
| Blue       | 32    |
| Green      | 28    |
| Yellow     | 19    |

## Table 9: Text-Heavy Table

| Question                          | Answer                                                                 |
|-----------------------------------|------------------------------------------------------------------------|
| What is RAG?                      | Retrieval-Augmented Generation combines search with LLM generation    |
| Why use vector databases?         | They enable semantic search over embeddings                            |
| What's the advantage of DuckDB?   | Embedded, file-based, supports VSS extension for HNSW indexes         |

## Table 10: Financial Data with Currencies

| Transaction | Date       | Amount   | Currency | Exchange_Rate | USD_Value |
|-------------|------------|----------|----------|---------------|-----------|
| TXN001      | 2024-01-15 | €1,250   | EUR      | 1.09          | $1,362.50 |
| TXN002      | 2024-01-16 | £875     | GBP      | 1.27          | $1,111.25 |
| TXN003      | 2024-01-17 | ¥125,000 | JPY      | 0.0067        | $837.50   |
| TXN004      | 2024-01-18 | $2,500   | USD      | 1.00          | $2,500.00 |

---

## Non-Table Content

This is regular markdown content that should be ignored by the table extractor.

- Bullet point 1
- Bullet point 2

Code block (not a table):
```
| This | Looks | Like | Table |
|------|-------|------|-------|
| But  | It's  | In   | Code  |
```

> Blockquote with | pipe | characters | that aren't a table.

**End of test file**
