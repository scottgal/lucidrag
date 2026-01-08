INSTALL sqlite;
LOAD sqlite;
ATTACH 'test-customers.sqlite' AS sqlite_db (TYPE sqlite);
CREATE TABLE sqlite_db.customers AS 
SELECT * FROM read_csv_auto('../pii-test.csv');
SELECT COUNT(*) FROM sqlite_db.customers;
