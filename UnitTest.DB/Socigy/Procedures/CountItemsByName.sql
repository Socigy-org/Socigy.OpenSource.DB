-- @returns scalar: int
-- @param name: string
SELECT COUNT(*) FROM {{TestItem}} WHERE {{TestItem.Name}} = @name
