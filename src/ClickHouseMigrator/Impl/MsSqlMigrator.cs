﻿using Dapper;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using System.Text;

namespace ClickHouseMigrator.Impl
{
	public class MsSqlMigrator : Migrator
	{
		private List<Column> _columns;
		private Options _options;
		private bool IsStarted;
		public MsSqlMigrator(Options options) : base(options)
		{
			IsStarted = false;
			_options = options;
		}
		protected override List<Column> GetColumns(string host, int port, string user, string pass, string database, string table)
		{
			if (_columns == null)
			{
				using (var conn = CreateDbConnection(host, port, user, pass, database))
				{					
					var primaryKeys = conn.Query($@"
SELECT column_name as PRIMARYKEYCOLUMN
FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS AS TC
INNER JOIN
	INFORMATION_SCHEMA.KEY_COLUMN_USAGE AS KU
		  ON TC.CONSTRAINT_TYPE = 'PRIMARY KEY' AND
			 TC.CONSTRAINT_NAME = KU.CONSTRAINT_NAME AND
			 KU.table_name = '{table}'
ORDER BY KU.TABLE_NAME, KU.ORDINAL_POSITION; ").ToList();
					
					_columns = conn.Query($"SELECT * FROM {database}.INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = N'{table}';")
						.Select(c =>
						{
							var dic = (IDictionary<string, dynamic>)c;						
							return new Column
							{
								DataType = dic["DATA_TYPE"],
								IsPrimary = primaryKeys.Select(f => f.PRIMARYKEYCOLUMN).ToList().Contains(dic["COLUMN_NAME"]),
								Name = dic["COLUMN_NAME"]
							};
						}).ToList();
					
				}
			}
			return _columns;
		}

		protected override string ConvertToClickHouserDataType(string type)
		{
			var sizePrefixIndex = type.IndexOf('(');
			var normalTypeName = sizePrefixIndex <= 0 ? type : type.Substring(0, sizePrefixIndex);
			switch (normalTypeName.ToLower())
			{
				case "timestamp":
				case "datetime2":
				case "datetime":
					{
						return "DateTime";
					}
				case "date":
					{
						return "Date";
					}
				case "tinyint":
					{
						return "UInt8";
					}
				case "smallint":
					{
						return "Int16";
					}
				case "int":
					{
						return "Int32";
					}
				case "float":
					{
						return "Float32";
					}
				case "double":
				case "decimal":
					{
						return "Float64";
					}
				case "bigint":
					{
						return "Int64";
					}
				case "varchar":
				case "nvarchar":
				default:
					{
						return "String";
					}
			}
		}

		protected override IDbConnection CreateDbConnection(string host, int port, string user, string pass, string database)
		{
			if (string.IsNullOrWhiteSpace(user))
			{
				user = "root";
			}
			if (port <= 0)
			{
				port = 1433;
			}
			if (string.IsNullOrWhiteSpace(database))
			{
				throw new ArgumentNullException("database");
			}
			var passwordPart = string.IsNullOrWhiteSpace(pass) ? "" : $"Password={pass};";

			var connectString =
				$"Server=tcp:{host},{port};Initial Catalog={database};Persist Security Info=False;User ID={user};{passwordPart}MultipleActiveResultSets=False;Encrypt=False;TrustServerCertificate=True;Connection Timeout=300;";

			var conn = new SqlConnection(connectString);
			return conn;
		}

		private string GetSelectPrimaryKeySql(string primaryKeysSql, string tableSql, int batch, int batchCount)
		{
			var skipRowsCount = batch * batchCount;

			if (!_options.Drop&&_options.Start>0&&IsStarted)
			{
				skipRowsCount = _options.Start;
				IsStarted = true;
			}
			return $@"SELECT {primaryKeysSql} FROM {tableSql} ORDER BY {primaryKeysSql} OFFSET ({skipRowsCount}) ROWS FETCH NEXT {batchCount} ROWS ONLY;";			
		}

		protected override Tuple<IDbCommand, int> GenerateBatchQueryCommand(IDbConnection conn, List<Column> primaryKeys,
			string selectColumnsSql, string tableSql, int batch, int batchCount)
		{
			var primaryKeysSql = GeneratePrimaryKeysSql(primaryKeys);
			var sql = GetSelectPrimaryKeySql(primaryKeysSql, tableSql, batch, batchCount);
			var primaries = conn.Query(sql).Select(d => d as IDictionary<string, dynamic>).ToArray();
			if (primaries.Length == 0)
			{
				return new Tuple<IDbCommand, int>(null, 0);
			}
			var command = conn.CreateCommand();
			StringBuilder builder = new StringBuilder();
			for (int j = 0; j < primaries.Length; ++j)
			{
				var values = primaries.ElementAt(j);
				builder.Append("(");
				for (int k = 0; k < primaryKeys.Count; ++k)
				{
					var parameterName = $"@P{j}";

					builder.Append(k == primaryKeys.Count - 1 ? $"{parameterName}" : $"{parameterName}, ");

					var parameter = command.CreateParameter();
					parameter.ParameterName = parameterName;
					parameter.Value = values[primaryKeys[k].Name];
					command.Parameters.Add(parameter);
				}
				builder.Append(j == primaries.Length - 1 ? ") " : "), ");
			}
			var inParameters = builder.Remove(builder.Length - 1, 1).ToString();			
			command.CommandText = $"SELECT {selectColumnsSql} FROM {tableSql} WHERE ({primaryKeysSql}) IN ({inParameters})";

			return new Tuple<IDbCommand, int>(command, primaries.Length);
		}

		protected override string GenerateQueryAllSql(string selectColumnsSql, string tableSql)
		{			
			return $"SELECT {selectColumnsSql} FROM {tableSql}";
		}
		public string GenerateResyncQueryAllSql(string selectColumnsSql, string tableSql, string primaryKey, List<int> notFilledRows)
		{			
			return $"SELECT {selectColumnsSql} FROM {tableSql} where {primaryKey} not in ({String.Join(",",notFilledRows)});";
		}

		private string GeneratePrimaryKeysSql(List<Column> columns)
		{
			return string.Join(", ", columns.Select(k => $"{k.Name}"));
		}

		protected override string GenerateTableSql(string database, string table)
		{			
			return $"{table}";
		}
		protected override string GenerateSelectColumnsSql(List<Column> columns)
		{
			return string.Join(',', columns.Select(c => $"{c.Name}"));
		}
	}
}
