from pathlib import Path
import hashlib
root = Path(__file__).resolve().parent
target = Path('C:\\Users\\28910\\.codex\\plugins\\cache\\openai-bundled\\computer-use\\26.609.41114\\scripts\\computer-use-client.mjs')
expected = 'aa7cf705c7b4c93ea52d8f2ac6dd81de841a3297da0e4b53c78cebd117d35a7b'
assert hashlib.sha256(target.read_bytes()).hexdigest() == expected, "Wrapper changed; inspect before restoring"
target.write_bytes((root / "computer-use-client.mjs.original").read_bytes())
print("Original wrapper restored")
