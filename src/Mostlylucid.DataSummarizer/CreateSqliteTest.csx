#r "nuget: Microsoft.Data.Sqlite, 9.0.0"

using Microsoft.Data.Sqlite;

var dbPath = "test-customers.sqlite";
if (File.Exists(dbPath)) File.Delete(dbPath);

var conn = new SqliteConnection($"Data Source={dbPath}");
conn.Open();

var cmd = conn.CreateCommand();
cmd.CommandText = @"
CREATE TABLE customers (
    id INTEGER PRIMARY KEY,
    name TEXT NOT NULL,
    email TEXT NOT NULL,
    phone TEXT,
    balance REAL,
    active INTEGER
);

INSERT INTO customers (name, email, phone, balance, active) VALUES
('John Doe', 'john@example.com', '555-123-4567', 1500.50, 1),
('Jane Smith', 'jane@company.org', '555-234-5678', 2300.00, 1),
('Bob Wilson', 'bob@test.com', '555-345-6789', 500.25, 0),
('Alice Brown', 'alice@domain.net', '555-456-7890', 10000.00, 1),
('Charlie Davis', 'charlie@mail.com', '555-567-8901', 750.00, 0),
('Diana Evans', 'diana@work.io', '555-678-9012', 3200.75, 1),
('Edward Frank', 'ed@home.com', '555-789-0123', 890.00, 1),
('Fiona Green', 'fiona@office.net', '555-890-1234', 4500.00, 0),
('George Hill', 'george@corp.com', '555-901-2345', 1250.50, 1),
('Hannah Ivy', 'hannah@firm.org', '555-012-3456', 6700.25, 1);
";
cmd.ExecuteNonQuery();
conn.Close();

Console.WriteLine("Created " + dbPath + " with 10 rows");
