"""Compare the Git index/commit with a local sealed Unity build snapshot.

No Unity execution or game state changes. Output is local, under Logs.
"""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess


def git(*args):
    return subprocess.check_output(['git', *args])


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--snapshot', required=True)
    parser.add_argument('--revision', default='INDEX')
    parser.add_argument('--output', required=True)
    args = parser.parse_args()
    root = Path(git('rev-parse', '--show-toplevel').decode().strip())
    expected = {}
    for line in Path(args.snapshot).read_text(encoding='utf-8-sig').splitlines():
        name, sha = line.rsplit('=', 1)
        expected[name.replace('\\', '/')] = sha.replace('-', '').lower()
    if args.revision == 'INDEX':
        records = git('ls-files', '-s', '-z').split(b'\0')
        indexed = {row.split(b'\t', 1)[1].decode(): row.split()[1].decode()
                   for row in records if row}
    else:
        records = git('ls-tree', '-r', '-z', args.revision).split(b'\0')
        indexed = {row.split(b'\t', 1)[1].decode(): row.split()[2].decode()
                   for row in records if row}
    controlled, excluded, errors = [], [], []
    for name, expected_sha in sorted(expected.items()):
        path = root / name
        if not path.is_file():
            errors.append({'path': name, 'reason': 'missing local input'})
            continue
        data = path.read_bytes()
        sha = hashlib.sha256(data).hexdigest()
        if sha != expected_sha:
            errors.append({'path': name, 'reason': 'local snapshot drift'})
        if name in indexed:
            oid = hashlib.sha1(b'blob ' + str(len(data)).encode() + b'\0' + data).hexdigest()
            if indexed[name] != oid:
                errors.append({'path': name, 'reason': 'Git blob differs from local bytes'})
            controlled.append({'path': name, 'sha256': sha})
        else:
            ignored = subprocess.run(['git', 'check-ignore', '-q', '--', name]).returncode == 0
            excluded.append({'path': name, 'sha256': sha, 'ignored': ignored})
            if not ignored:
                errors.append({'path': name, 'reason': 'untracked nonignored input'})
    # Explicit portable digest: UTF-8 sorted path=lowercase SHA256, with LF including final LF.
    lines = ''.join(f"{row['path']}={row['sha256']}\n" for row in controlled)
    result = {'revision': args.revision, 'snapshotInputCount': len(expected),
              'controlledInputCount': len(controlled), 'excludedInputCount': len(excluded),
              'controlledInputDigest': hashlib.sha256(lines.encode('utf-8')).hexdigest(),
              'digestFormat': 'sorted path=lowercase SHA256, UTF-8, LF, final LF',
              'excludedInputs': excluded, 'errors': errors, 'passed': not errors}
    output = Path(args.output)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({k: v for k, v in result.items() if k not in ('excludedInputs', 'errors')}, ensure_ascii=False))
    if errors:
        print(json.dumps(errors[:20], ensure_ascii=False, indent=2))
        raise SystemExit(1)


if __name__ == '__main__':
    main()
