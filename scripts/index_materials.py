"""Read external materials without extracting, running or changing any original."""
import argparse
import hashlib
import json
from pathlib import Path
import zipfile


def inventory(directory):
    entries, first_hash = [], {}
    for path in sorted(directory.iterdir(), key=lambda p: p.name):
        if not path.is_file() or path.suffix.lower() not in {'.pdf', '.zip', '.7z', '.rar'}:
            continue
        digest = hashlib.sha256()
        with path.open('rb') as source:
            for chunk in iter(lambda: source.read(1024 * 1024), b''):
                digest.update(chunk)
        sha = digest.hexdigest()
        entry = {'name': path.name, 'bytes': path.stat().st_size, 'sha256': sha}
        if sha in first_hash:
            entry['duplicate_of'] = first_hash[sha]
        else:
            first_hash[sha] = path.name
        if path.suffix.lower() == '.zip':
            with zipfile.ZipFile(path) as archive:
                entry['members'] = [
                    {'path': info.filename, 'bytes': info.file_size}
                    for info in archive.infolist() if not info.is_dir()
                ]
        entries.append(entry)
    return {'schema': 1, 'scope': 'Top-level original files; ZIP immediate members only',
            'file_count': len(entries), 'unique_content_count': len(first_hash), 'files': entries}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('directory', type=Path)
    parser.add_argument('--output', required=True, type=Path)
    arguments = parser.parse_args()
    result = inventory(arguments.directory)
    arguments.output.parent.mkdir(parents=True, exist_ok=True)
    arguments.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(f"Indexed {result['file_count']} files / {result['unique_content_count']} unique contents")
