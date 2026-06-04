-- @returns: Benchmarks.BenchUser
-- @param max: int
SELECT "id", "name", "age", "created_at" FROM "bench_users" WHERE "age" < @max
