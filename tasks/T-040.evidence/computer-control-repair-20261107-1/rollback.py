from pathlib import Path
import hashlib
backup = Path(__file__).resolve().parent
target = Path('C:\\Users\\28910\\AppData\\Local\\OpenAI\\Codex\\runtimes\\cua_node\\c4873bdffed0b168\\bin\\node_modules\\@oai\\sky\\package.json')
expected = 'babaa17c7a26204a5937476427506545aa662703c68a90286cfc217a8730a407'
assert hashlib.sha256(target.read_bytes()).hexdigest() == expected, "Package changed since repair; inspect before restoring"
original = (backup / "package.json.original").read_bytes()
target.write_bytes(original)
print("Original package metadata restored")
