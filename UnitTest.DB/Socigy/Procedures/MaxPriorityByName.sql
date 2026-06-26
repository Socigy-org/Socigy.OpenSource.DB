-- @returns scalar: int?
-- @param name: string
SELECT MAX({{TestItem.Priority}}) FROM {{TestItem}} WHERE {{TestItem.Name}} = @name
