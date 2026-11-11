---
name: react-ts
description: "Use quando o pedido envolver frontend React com TypeScript: componente export default function, props, useState, useEffect com AbortController, fetch tipado, tabela e gráfico em SVG."
---

=== EXEMPLO 8 — React 19 + TypeScript + Bootstrap 5 (não copie: leia a dinâmica) ===

```tsx
// src/api/customers.ts — fetch tipado; erro vem do ProblemDetails
import type { CustomerDto } from "../types";
export async function listCustomers(page: number, signal?: AbortSignal): Promise<CustomerDto[]> {
  const res = await fetch(`/api/customers?page=${page}`, { signal });
  if (!res.ok) throw new Error(((await res.json()) as { title?: string }).title ?? res.statusText);
  return res.json() as Promise<CustomerDto[]>;
}
export async function createCustomer(body: { name: string; document: string }): Promise<CustomerDto> {
  const res = await fetch("/api/customers", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });
  if (!res.ok) throw new Error(((await res.json()) as { title?: string }).title ?? res.statusText);
  return res.json() as Promise<CustomerDto>;
}
```

```tsx
// src/pages/CustomersPage.tsx — um componente, um arquivo
import { useEffect, useState } from "react";
import type { CustomerDto } from "../types";
import { createCustomer, listCustomers } from "../api/customers";

export default function CustomersPage() {
  const [items, setItems] = useState<CustomerDto[]>([]);
  const [page, setPage] = useState(1);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [name, setName] = useState("");
  const [document, setDocument] = useState("");

  useEffect(() => {
    const controller = new AbortController();
    setLoading(true);
    listCustomers(page, controller.signal)
      .then(setItems)
      .catch((e: Error) => { if (e.name !== "AbortError") setError(e.message); })
      .finally(() => setLoading(false));
    return () => controller.abort();
  }, [page]);

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setError(null);
    try {
      setItems((prev) => [await createCustomer({ name, document }), ...prev]);
      setName(""); setDocument("");
    } catch (e) { setError((e as Error).message); }
  }

  return (
    <main className="container py-4">
      <h1 className="h4 mb-3">Customers</h1>
      {error && <div className="alert alert-danger" role="alert">{error}</div>}
      <form className="row g-2 mb-4" onSubmit={submit}>
        <div className="col-md-5">
          <label className="form-label" htmlFor="name">Name</label>
          <input id="name" className="form-control" value={name} onChange={(e) => setName(e.target.value)} required />
        </div>
        <div className="col-md-5">
          <label className="form-label" htmlFor="document">Document</label>
          <input id="document" className="form-control" value={document} onChange={(e) => setDocument(e.target.value)} required />
        </div>
        <div className="col-md-2 d-flex align-items-end">
          <button className="btn btn-primary w-100" type="submit">Add</button>
        </div>
      </form>
      {loading ? (
        <div className="spinner-border text-primary" role="status" aria-label="Loading" />
      ) : (
        <table className="table table-striped table-hover align-middle">
          <thead><tr><th>Name</th><th>Document</th></tr></thead>
          <tbody>
            {items.map((c) => (<tr key={c.id}><td>{c.name}</td><td>{c.document}</td></tr>))}
          </tbody>
        </table>
      )}
      <nav className="d-flex gap-2">
        <button className="btn btn-outline-secondary" disabled={page === 1} onClick={() => setPage((p) => p - 1)}>Previous</button>
        <button className="btn btn-outline-secondary" onClick={() => setPage((p) => p + 1)}>Next</button>
      </nav>
    </main>
  );
}
```

Bootstrap sem lib extra de tabela: `npm i bootstrap` e `import "bootstrap/dist/css/bootstrap.min.css";` no `main.tsx`.
`AbortController` no efeito evita resposta fora de ordem ao trocar de página; erro por widget, não global.
