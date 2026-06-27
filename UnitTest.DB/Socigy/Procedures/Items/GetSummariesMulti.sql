-- @returns: UnitTest.DB.ItemSummaryMulti
-- @param name: string
SELECT {{TestItem.Name}} AS "Name", {{TestItem.Priority}} AS "Priority" FROM {{TestItem}} WHERE {{TestItem.Name}} = @name
