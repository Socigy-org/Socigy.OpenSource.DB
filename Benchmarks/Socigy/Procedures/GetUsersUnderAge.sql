-- @returns: Benchmarks.BenchUser
-- @param max: int
SELECT {{BenchUser.Id}}, {{BenchUser.Name}}, {{BenchUser.Age}}, {{BenchUser.CreatedAt}} 
FROM {{BenchUser}} 
WHERE {{BenchUser.Age}} < @max
