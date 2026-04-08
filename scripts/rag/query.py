import chromadb
import os
import sys
import json

DB_PATH = os.path.expanduser("~/.office-rag-db")

def query_codebase(question, n_results=5):
    client = chromadb.PersistentClient(path=DB_PATH)
    collection = client.get_collection("codebase")

    results = collection.query(
        query_texts=[question],
        n_results=n_results
    )

    output = []
    for i in range(len(results["ids"][0])):
        output.append({
            "id": results["ids"][0][i],
            "file": results["metadatas"][0][i]["file"],
            "repo": results["metadatas"][0][i]["repo"],
            "content": results["documents"][0][i][:500]  # truncate for prompt size
        })

    return output

if __name__ == "__main__":
    question = sys.argv[1] if len(sys.argv) > 1 else "What areas of code need improvement?"
    results = query_codebase(question)
    print(json.dumps(results, indent=2))
