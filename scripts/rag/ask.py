import chromadb
import os
import sys
import json
import urllib.request


def _resolve_state_root() -> str:
    env_val = os.environ.get("FOUNDRY_STATE_ROOT", "")
    if env_val:
        return env_val
    if sys.platform == "win32":
        return r"C:\FoundryState"
    return os.path.join(os.path.expanduser("~"), "foundry-state")


DB_PATH = _resolve_state_root()
OLLAMA_URL = "http://localhost:11434/api/generate"

def query_rag(question, n_results=5):
    client = chromadb.PersistentClient(path=DB_PATH)
    collection = client.get_collection("codebase")
    results = collection.query(query_texts=[question], n_results=n_results)

    context = []
    for i in range(len(results["ids"][0])):
        file = results["metadatas"][0][i]["file"]
        code = results["documents"][0][i][:800]
        context.append(f"### {file}\n```\n{code}\n```")

    return "\n\n".join(context)

def ask_ollama(prompt, model=None):
    if model is None:
        model = os.environ.get("FOUNDRY_SCORING_MODEL", "deepseek-r1:14b")
    payload = json.dumps({
        "model": model,
        "prompt": prompt,
        "stream": False,
        "options": {"temperature": 0.4}
    }).encode("utf-8")

    req = urllib.request.Request(OLLAMA_URL, data=payload, headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=120) as resp:
        return json.loads(resp.read().decode("utf-8"))["response"]

if __name__ == "__main__":
    question = sys.argv[1] if len(sys.argv) > 1 else "What areas of the codebase need improvement?"

    # Step 1: Search codebase
    context = query_rag(question)

    # Step 2: Ask Ollama with code context
    prompt = f"""You are a senior software engineer reviewing a codebase.
Based on the following code snippets from the repository, answer this question:

{question}

--- RELEVANT CODE FROM THE CODEBASE ---
{context}
--- END CODE ---

Be specific. Reference actual file names and code you see above."""

    answer = ask_ollama(prompt)
    print(answer)
