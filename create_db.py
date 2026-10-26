import sqlite3

DB_PATH = "/tmp/opencode/dual-opencode-80926/sqlite/probe.db"

try:
    # Conecta ou cria o banco de dados
    conn = sqlite3.connect(DB_PATH)
    cursor = conn.cursor()

    # Cria a tabela se ela não existir
    cursor.execute("CREATE TABLE IF NOT EXISTS itens (id INTEGER PRIMARY KEY, nome TEXT)")

    # Insere os registros
    items_to_insert = [('alfa',), ('beta',)]
    cursor.executemany("INSERT INTO itens (nome) VALUES (?)", items_to_insert)

    # Commit das alterações
    conn.commit()

    # Conta os registros
    cursor.execute("SELECT COUNT(*) FROM itens")
    count = cursor.fetchone()[0]

    # Fecha a conexão
    conn.close()

    print(f"SQLITE-OK\n{count}")

except sqlite3.Error as e:
    print(f"ERRO_DB: {e}")
finally:
    # Garante que o arquivo de banco de dados exista no caminho especificado, mesmo em caso de erro
    import os
    os.makedirs(os.path.dirname(DB_PATH), exist_ok=True)