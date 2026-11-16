"""The one reader of src/database-schema.ts, bounded as ReleaseManifest.Valid."""
import os
from pathlib import Path
import tempfile
import unittest
from installer.windows import database_schema as schema

ROOT = Path(__file__).resolve().parents[1]


class SourceSchemaTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix='yahad-schema-', dir=os.environ.get('YAHAD_TEST_TEMP'))
        self.addCleanup(self.temporary.cleanup)

    def source(self, text):
        root = Path(self.temporary.name)
        (root/'src').mkdir(exist_ok=True)
        (root/'src'/'database-schema.ts').write_text(text, encoding='utf-8')
        # Store imports the constant; it deliberately has no numeric declaration.
        (root/'src'/'store.ts').write_text("import { DATABASE_SCHEMA } from './db.js';", encoding='utf-8')
        return root

    def declare(self, target, sources):
        return self.source(f'export const DATABASE_SCHEMA = {target};\nexport const DATABASE_MIGRATIONS_FROM = [{sources}] as const;\n')

    def test_checked_in_declaration_is_read(self):
        target, migrations = schema.source_schema(ROOT)
        self.assertGreaterEqual(target, 1); self.assertTrue(all(1 <= v < target for v in migrations))

    def test_schema_and_sources_are_derived_from_the_declaration(self):
        self.assertEqual(schema.source_schema(self.declare(6, '1, 2, 3, 4, 5')), (6, [1, 2, 3, 4, 5]))
        self.assertEqual(schema.source_schema(self.declare(1, '')), (1, []))
        self.assertEqual(schema.source_schema(self.declare(1024, ', '.join(str(v) for v in range(993, 1024)))), (1024, list(range(993, 1024))))

    def test_missing_duplicate_or_out_of_bounds_declarations_are_refused(self):
        for text in ('', 'export const DATABASE_SCHEMA = 6;', 'export const DATABASE_MIGRATIONS_FROM = [1] as const;',
                     'export const DATABASE_SCHEMA = 6;\nexport const DATABASE_SCHEMA = 5;\nexport const DATABASE_MIGRATIONS_FROM = [] as const;\n',
                     '// export const DATABASE_SCHEMA = 6;\nexport const DATABASE_MIGRATIONS_FROM = [] as const;\n'):
            with self.subTest(text=text), self.assertRaises(schema.SchemaError): schema.source_schema(self.source(text))
        for target, sources in ((0, ''), (1025, ''), (6, '2, 1'), (6, '1, 1'), (6, '0, 1'), (6, '1, 6'), (6, '01'),
                                (64, ', '.join(str(v) for v in range(1, 34)))):
            with self.subTest(target=target, sources=sources), self.assertRaises(schema.SchemaError): schema.source_schema(self.declare(target, sources))

    def test_build_record_cannot_disagree_with_the_declaration(self):
        root = self.declare(6, '1, 2')
        self.assertEqual(schema.require_build_schema({'databaseSchema':6, 'databaseMigrationsFrom':[1, 2]}, root), (6, [1, 2]))
        for build in ({'databaseSchema':5, 'databaseMigrationsFrom':[1, 2]}, {'databaseSchema':True, 'databaseMigrationsFrom':[1, 2]},
                      {'databaseSchema':'6', 'databaseMigrationsFrom':[1, 2]}, {'databaseSchema':6, 'databaseMigrationsFrom':[1]},
                      {'databaseSchema':6, 'databaseMigrationsFrom':[1, True]}, {'databaseSchema':6}, {'databaseSchema':6, 'databaseMigrationsFrom':None}):
            with self.subTest(build=build), self.assertRaisesRegex(schema.SchemaError, 'schema'): schema.require_build_schema(build, root)


if __name__ == '__main__':
    unittest.main()
