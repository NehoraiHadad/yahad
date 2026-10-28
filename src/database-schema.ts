// Shared by application readiness, storage and offline release packaging.
export const DATABASE_SCHEMA = 2;
// Each declared source requires a matching production smoke migration fixture.
export const DATABASE_MIGRATIONS_FROM = [1] as const;
