#r "nuget: Microsoft.Data.Sqlite, 9.0.0"

using Microsoft.Data.Sqlite;

var dbPath = "bank_churn.sqlite";
var conn = new SqliteConnection($"Data Source={dbPath}");
conn.Open();

// Check tables
var cmd = conn.CreateCommand();
cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
var reader = cmd.ExecuteReader();
Console.WriteLine("Tables:");
while (reader.Read()) Console.WriteLine($"  - {reader.GetString(0)}");
reader.Close();

// Check indexes
cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='index' AND name NOT LIKE 'sqlite_%'";
reader = cmd.ExecuteReader();
Console.WriteLine("\nIndexes:");
while (reader.Read()) Console.WriteLine($"  - {reader.GetString(0)}");
reader.Close();

// Check row count
cmd.CommandText = "SELECT COUNT(*) FROM Bank_Churn";
var count = cmd.ExecuteScalar();
Console.WriteLine($"\nRow count: {count}");

// Sample data
cmd.CommandText = "SELECT CustomerId, Surname, Geography, Gender, Age, Exited FROM Bank_Churn LIMIT 5";
reader = cmd.ExecuteReader();
Console.WriteLine("\nSample data:");
while (reader.Read())
{
    Console.WriteLine($"  {reader.GetInt64(0)}, {reader.GetString(1)}, {reader.GetString(2)}, {reader.GetString(3)}, Age={reader.GetDouble(4)}, Exited={reader.GetInt32(5)}");
}
reader.Close();
conn.Close();
