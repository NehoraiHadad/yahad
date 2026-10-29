// Shared by application readiness, storage and offline release packaging.
export const DATABASE_SCHEMA = 6;
// Each declared source requires a matching production smoke migration fixture.
export const DATABASE_MIGRATIONS_FROM = [1, 2, 3, 4, 5] as const;
