import chromadb
import os
import sys
import time


def _resolve_state_root() -> str:
    env_val = os.environ.get("FOUNDRY_STATE_ROOT", "")
    if env_val:
        return env_val
    if sys.platform == "win32":
        return r"C:\FoundryState"
    return os.path.join(os.path.expanduser("~"), "foundry-state")


DB_PATH = _resolve_state_root()
REPO_ROOT = os.environ.get("FOUNDRY_REPO_ROOT", os.path.expanduser("~/OneDrive/Documents/GitHub"))
EXTENSIONS = {".ts", ".tsx", ".js", ".jsx", ".py", ".ps1", ".json", ".md", ".yml", ".yaml", ".css", ".html", ".sh"}
SKIP_DIRS = {"node_modules", ".git", "dist", "build", ".next", "__pycache__", ".venv", "venv"}
MAX_FILE_SIZE = 50_000

def chunk_file(content, max_chars=2000):
    lines = content.split("\n")
    chunks = []
    current = []
    current_len = 0

    for line in lines:
        current.append(line)
        current_len += len(line) + 1
        if current_len >= max_chars:
            chunks.append("\n".join(current))
            current = current[-5:]
            current_len = sum(len(l) + 1 for l in current)

    if current:
        chunks.append("\n".join(current))

    return chunks

def index_repos():
    client = chromadb.PersistentClient(path=DB_PATH)

    try:
        client.delete_collection("codebase")
    except:
        pass

    collection = client.create_collection("codebase")

    file_count = 0
    chunk_count = 0

    # Collect everything first, then batch upsert
    all_ids = []
    all_docs = []
    all_metas = []

    for repo in ["Foundry"]:  # Suite excluded while building ML training base — will re-add with separate config
        repo_path = os.path.join(REPO_ROOT, repo)
        if not os.path.exists(repo_path):
            print(f"  Skipping {repo} - not found")
            continue

        for root, dirs, files in os.walk(repo_path):
            dirs[:] = [d for d in dirs if d not in SKIP_DIRS]

            for f in files:
                filepath = os.path.join(root, f)
                ext = os.path.splitext(filepath)[1].lower()

                if ext not in EXTENSIONS:
                    continue

                if os.path.getsize(filepath) > MAX_FILE_SIZE:
                    continue

                try:
                    with open(filepath, "r", encoding="utf-8", errors="ignore") as fh:
                        content = fh.read()

                    if not content.strip():
                        continue

                    rel_path = os.path.relpath(filepath, REPO_ROOT)
                    chunks = chunk_file(content)

                    for i, chunk in enumerate(chunks):
                        all_ids.append(f"{rel_path}::chunk{i}")
                        all_docs.append(chunk)
                        all_metas.append({"repo": repo, "file": rel_path, "chunk": i})
                        chunk_count += 1

                    file_count += 1
                except Exception as e:
                    print(f"  Error reading {filepath}: {e}")

    # Batch upsert in groups of 100
    BATCH = 100
    for i in range(0, len(all_ids), BATCH):
        end = min(i + BATCH, len(all_ids))
        collection.upsert(
            ids=all_ids[i:end],
            documents=all_docs[i:end],
            metadatas=all_metas[i:end]
        )
        print(f"  Indexed batch {i//BATCH + 1} ({end}/{len(all_ids)} chunks)")

    print(f"Indexed {file_count} files ({chunk_count} chunks) into {DB_PATH}")

if __name__ == "__main__":
    start = time.time()
    index_repos()
    print(f"  Completed in {time.time() - start:.1f}s")
