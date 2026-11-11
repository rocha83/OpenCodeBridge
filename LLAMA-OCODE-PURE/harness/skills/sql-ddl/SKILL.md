---
name: sql-ddl
description: "Use quando o pedido envolver banco SQL: CREATE TABLE com PRIMARY KEY, UNIQUE, CHECK e FOREIGN KEY, índices, seeds DML e consultas que casam com o modelo."
---

=== EXEMPLO 7 — SQL: DDL e DML das entidades (não copie: leia a dinâmica) ===
```sql
-- schema.sql: entidades, constraints e índices
CREATE TABLE Customers (
    Id        TEXT PRIMARY KEY,
    Name      TEXT NOT NULL CHECK (length(trim(Name)) > 0),
    Document  TEXT NOT NULL UNIQUE,
    CreatedAt TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE TABLE Orders (
    Id         TEXT PRIMARY KEY,
    CustomerId TEXT NOT NULL REFERENCES Customers (Id) ON DELETE CASCADE,
    Total      NUMERIC NOT NULL CHECK (Total >= 0),
    CreatedAt  TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IX_Orders_CustomerId ON Orders (CustomerId);
CREATE INDEX IX_Orders_CreatedAt ON Orders (CreatedAt);

-- seed.sql: dados de exemplo
INSERT INTO Customers (Id, Name, Document) VALUES ('c1', 'Maria', '52998224725');
INSERT INTO Orders (Id, CustomerId, Total) VALUES ('o1', 'c1', 149.90), ('o2', 'c1', 89.00);

-- dml.sql: consulta, alteração e remoção do dia a dia
SELECT c.Name, count(o.Id) AS Orders, coalesce(sum(o.Total), 0) AS Total
FROM Customers c LEFT JOIN Orders o ON o.CustomerId = c.Id
GROUP BY c.Id ORDER BY Total DESC;

UPDATE Orders SET Total = Total * 1.1
WHERE CustomerId = 'c1' AND CreatedAt >= date('now', '-30 day');

DELETE FROM Orders WHERE CustomerId = 'c1' AND Total = 0;

-- upsert (SQLite/Postgres)
INSERT INTO Customers (Id, Name, Document) VALUES ('c1', 'Maria Silva', '52998224725')
ON CONFLICT (Id) DO UPDATE SET Name = excluded.Name;
```

Regras: `UPDATE`/`DELETE` sempre com `WHERE`; escrita multi-tabela em `BEGIN; ... COMMIT;` com `ROLLBACK` no erro; seed não repete chave existente.
