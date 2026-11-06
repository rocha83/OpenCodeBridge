import json
import re

def extract_dialogue(json_path, md_path):
    """
    Lê um arquivo JSON, extrai diálogos de usuários e assistentes usando regex,
    e grava a sequência em um arquivo Markdown.
    """
    try:
        with open(json_path, 'r', encoding='utf-8') as f:
            data = json.load(f)
    except FileNotFoundError:
        print(f"ERRO: Arquivo não encontrado em {json_path}")
        return
    except json.JSONDecodeError:
        print(f"ERRO: Falha ao decodificar JSON em {json_path}. Verifique a sintaxe.")
        return

    dialogue = []
    messages = data.get('messages', [])

    for msg in messages:
        msg_type = msg.get('type')
        if msg_type not in ['user', 'assistant']:
            continue

        text_content = ""
        
        # 1. Tentativa de obter texto direto (para mensagens simples)
        if msg.get('text'):
            text_content = msg['text']
        
        # 2. Tentativa de extrair conteúdo do array 'content' (mais comum em respostas complexas)
        elif msg.get('content') and isinstance(msg['content'], list):
            content_parts = []
            for part in msg['content']:
                if part.get('type') == 'text':
                    content_parts.append(part.get('text', ''))
                elif part.get('type') == 'reasoning':
                    # Adiciona o reasoning com um prefixo claro
                    content_parts.append(f"[REASONING]: {part.get('text', '')}")
            
            if content_parts:
                text_content = "\\n".join(content_parts)

        if text_content:
            # Limpeza básica de caracteres de escape que podem vir do JSON (ex: \\n -> \n)
            cleaned_text = text_content.replace('\\n', '\n').replace('\\r', '')
            dialogue.append({
                'role': msg_type.upper(),
                'text': cleaned_text.strip()
            })

    # Gravar o diálogo no arquivo Markdown
    try:
        with open(md_path, 'w', encoding='utf-8') as md_file:
            for item in dialogue:
                # Formato desejado: ROLE: Conteúdo
                md_file.write(f"{item['role']}: {item['text']}\\n")
        print(f"SUCESSO: Diálogo extraído e salvo em {md_path}")
    except Exception as e:
        print(f"ERRO ao escrever o arquivo Markdown: {e}")

# --- Execução ---
JSON_FILE = '/home/mint/Downloads/Sessao 30 - Plano-para-retomar-testes-do-e-tools-full.json'
MD_FILE = '/home/mint/Downloads/Sessao 30 - Plano-para-retomar-testes-do-e-tools-full_RESUMO.md'

extract_dialogue(JSON_FILE, MD_FILE)