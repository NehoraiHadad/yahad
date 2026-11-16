"""The one reader of the app's database declaration, src/database-schema.ts.

It feeds the app's build record (prepare_core_fixture.py), the install package
(setup_package.py) and through it the format-2 release manifest
(release_manifest.py). Bounds are ReleaseManifest.Valid's
(installer/Yahad.Install.Core/ReleaseManifest.cs): a schema of 1..1024 and at most
32 migration sources, ascending and unique, each 1..schema-1 (the Linux rule,
deploy/updater.py).
"""
from pathlib import Path
import re

SOURCE = 'src/database-schema.ts'
MAXIMUM_SCHEMA, MAXIMUM_SOURCES = 1024, 32
_SCHEMA = re.compile(r'^export const DATABASE_SCHEMA\s*=\s*([0-9]+);', re.M)
_SOURCES = re.compile(r'^export const DATABASE_MIGRATIONS_FROM\s*=\s*\[([0-9,\s]*)\]\s*as const;', re.M)


class SchemaError(ValueError): pass


def parse(text):
    """(schema, migrations) of the declaration text; each declared exactly once."""
    schema, sources = _SCHEMA.findall(text), _SOURCES.findall(text)
    if len(schema) != 1 or len(sources) != 1: raise SchemaError('Source schema declaration required')
    if not re.fullmatch(r'[1-9][0-9]{0,3}', schema[0]): raise SchemaError('Source schema declaration invalid')
    target = int(schema[0])
    values = [value.strip() for value in sources[0].split(',')]
    if values == ['']: values = []
    if any(not re.fullmatch(r'[1-9][0-9]{0,3}', value) for value in values): raise SchemaError('Source migration declaration invalid')
    migrations = [int(value) for value in values]
    if (target > MAXIMUM_SCHEMA or len(migrations) > MAXIMUM_SOURCES or migrations != sorted(set(migrations)) or
            any(not 1 <= value < target for value in migrations)):
        raise SchemaError('Source migration declaration invalid')
    return target, migrations


def source_schema(repo):
    path = Path(repo)/SOURCE
    if path.is_symlink() or not path.is_file() or path.stat().st_size > 65536: raise SchemaError('Source schema declaration required')
    return parse(path.read_text(encoding='utf-8'))


def require_build_schema(build, repo):
    """A build record (app/legal/build-inputs.json) names exactly the source declaration."""
    schema, migrations = source_schema(repo)
    recorded = build.get('databaseMigrationsFrom') if isinstance(build, dict) else None
    if (type(build.get('databaseSchema')) is not int or build['databaseSchema'] != schema or not isinstance(recorded, list) or
            any(type(value) is not int for value in recorded) or recorded != migrations):
        raise SchemaError('Mixed source database schema')
    return schema, migrations
