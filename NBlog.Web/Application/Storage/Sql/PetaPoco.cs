﻿/* PetaPoco - A Tiny ORMish thing for your POCO's.
 * Copyright © 2011 Topten Software.  All Rights Reserved.
 * 
 * Apache License 2.0 - http://www.toptensoftware.com/petapoco/license
 * 
 * Special thanks to Rob Conery (@robconery) for original inspiration (ie:Massive) and for 
 * use of Subsonic's T4 templates, Rob Sullivan (@DataChomp) for hard core DBA advice 
 * and Adam Schroder (@schotime) for lots of suggestions, improvements and Oracle support
 */

// Define PETAPOCO_NO_DYNAMIC in your project settings on .NET 3.5

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Configuration;
using System.Data.Common;
using System.Data;
using System.Text.RegularExpressions;
using System.Reflection;
using System.Reflection.Emit;

namespace PetaPoco
{
	// Poco's marked [Explicit] require all column properties to be marked
	[AttributeUsage(AttributeTargets.Class)]
	public class ExplicitColumnsAttribute : Attribute
	{
	}
	// For non-explicit pocos, causes a property to be ignored
	[AttributeUsage(AttributeTargets.Property)]
	public class IgnoreAttribute : Attribute
	{
	}

	// For explicit pocos, marks property as a column and optionally supplies column name
	[AttributeUsage(AttributeTargets.Property)]
	public class ColumnAttribute : Attribute
	{
		public ColumnAttribute() { }
		public ColumnAttribute(string name) { Name = name; }
		public string Name { get; set; }
	}

	// For explicit pocos, marks property as a result column and optionally supplies column name
	[AttributeUsage(AttributeTargets.Property)]
	public class ResultColumnAttribute : ColumnAttribute
	{
		public ResultColumnAttribute() { }
		public ResultColumnAttribute(string name) : base(name) { }
	}

	// Specify the table name of a poco
	[AttributeUsage(AttributeTargets.Class)]
	public class TableNameAttribute : Attribute
	{
		public TableNameAttribute(string tableName)
		{
			Value = tableName;
		}
		public string Value { get; private set; }
	}

	// Specific the primary key of a poco class (and optional sequence name for Oracle)
	[AttributeUsage(AttributeTargets.Class)]
	public class PrimaryKeyAttribute : Attribute
	{
		public PrimaryKeyAttribute(string primaryKey)
		{
			Value = primaryKey;
		}

		public string Value { get; private set; }
		public string sequenceName { get; set; }
	}

	// Results from paged request
	public class Page<T> where T : new()
	{
		public long CurrentPage { get; set; }
		public long TotalPages { get; set; }
		public long TotalItems { get; set; }
		public long ItemsPerPage { get; set; }
		public List<T> Items { get; set; }
	}

	public class AnsiString
	{
		public AnsiString(string str)
		{
			Value = str;
		}
		public string Value { get; private set; }
	}

	// Optionally provide and implementation of this to Database.Mapper
	public interface IMapper
	{
		void GetTableInfo(Type t, ref string tableName, ref string primaryKey, ref string sequenceName);
		bool MapPropertyToColumn(PropertyInfo pi, ref string columnName, ref bool resultColumn);
		Func<object, object> GetFromDbConverter(PropertyInfo pi, Type SourceType);
		Func<object, object> GetToDbConverter(Type SourceType);
	}

	// Database class ... this is where most of the action happens
	public class Database : IDisposable
	{
		public Database(IDbConnection connection)
		{
			_sharedConnection = connection;
			_connectionString = connection.ConnectionString;
			_sharedConnectionDepth = 2;		// Prevent closing external connection
			CommonConstruct();
		}

		public Database(string connectionString, string providerName)
		{
			_connectionString = connectionString;
			_providerName = providerName;
			CommonConstruct();
		}

		public Database(string connectionString, DbProviderFactory provider)
		{
			_connectionString = connectionString;
			_factory = provider;
			CommonConstruct();
		}

		public Database(string connectionStringName)
		{
			// Use first?
			if (connectionStringName == "")
				connectionStringName = ConfigurationManager.ConnectionStrings[0].Name;

			// Work out connection string and provider name
			var providerName = "System.Data.SqlClient";
			if (ConfigurationManager.ConnectionStrings[connectionStringName] != null)
			{
				if (!string.IsNullOrEmpty(ConfigurationManager.ConnectionStrings[connectionStringName].ProviderName))
					providerName = ConfigurationManager.ConnectionStrings[connectionStringName].ProviderName;
			}
			else
			{
				throw new InvalidOperationException("Can't find a connection string with the name '" + connectionStringName + "'");
			}

			// Store factory and connection string
			_connectionString = ConfigurationManager.ConnectionStrings[connectionStringName].ConnectionString;
			_providerName = providerName;
			CommonConstruct();
		}

		enum DBType
		{
			SqlServer,
			SqlServerCE,
			MySql,
			PostgreSQL,
			Oracle,
		}
		DBType _dbType = DBType.SqlServer;

		// Common initialization
		private void CommonConstruct()
		{
			_transactionDepth = 0;
			EnableAutoSelect = true;
			EnableNamedParams = true;
			ForceDateTimesToUtc = true;

			if (_providerName != null)
				_factory = DbProviderFactories.GetFactory(_providerName);

			string dbtype = (_factory == null ? _sharedConnection.GetType() : _factory.GetType()).Name;
			if (dbtype.StartsWith("MySql")) _dbType = DBType.MySql;
			else if (dbtype.StartsWith("SqlCe")) _dbType = DBType.SqlServerCE;
			else if (dbtype.StartsWith("Npgsql")) _dbType = DBType.PostgreSQL;
			else if (dbtype.StartsWith("Oracle")) _dbType = DBType.Oracle;

			if (_dbType == DBType.MySql && _connectionString != null && _connectionString.IndexOf("Allow User Variables=true") >= 0)
				_paramPrefix = "?";
			if (_dbType == DBType.Oracle)
				_paramPrefix = ":";
		}

		// Automatically close one open shared connection
		public void Dispose()
		{
			if (_sharedConnectionDepth > 0)
				CloseSharedConnection();
		}

		// Open a connection (can be nested)
		public void OpenSharedConnection()
		{
			if (_sharedConnectionDepth == 0)
			{
				_sharedConnection = _factory.CreateConnection();
				_sharedConnection.ConnectionString = _connectionString;
				_sharedConnection.Open();
			}
			_sharedConnectionDepth++;
		}

		// Close a previously opened connection
		public void CloseSharedConnection()
		{
			_sharedConnectionDepth--;
			if (_sharedConnectionDepth == 0)
			{
				_sharedConnection.Dispose();
				_sharedConnection = null;
			}
		}

		// Access to our shared connection
		public IDbConnection Connection
		{
			get { return _sharedConnection; }
		}

		// Helper to create a transaction scope
		public Transaction Transaction
		{
			get	{ return new Transaction(this);	}
		}

		// Use by derived repo generated by T4 templates
		public virtual void OnBeginTransaction() { }
		public virtual void OnEndTransaction() { }

		// Start a new transaction, can be nested, every call must be
		//	matched by a call to AbortTransaction or CompleteTransaction
		// Use `using (var scope=db.Transaction) { scope.Complete(); }` to ensure correct semantics
		public void BeginTransaction()
		{
			_transactionDepth++;

			if (_transactionDepth == 1)
			{
				OpenSharedConnection();
				_transaction = _sharedConnection.BeginTransaction();
				_transactionCancelled = false;
				OnBeginTransaction();
			}

		}

		// Internal helper to cleanup transaction stuff
		void CleanupTransaction()
		{
			OnEndTransaction();

			if (_transactionCancelled)
				_transaction.Rollback();
			else
				_transaction.Commit();

			_transaction.Dispose();
			_transaction = null;

			CloseSharedConnection();
		}

		// Abort the entire outer most transaction scope
		public void AbortTransaction()
		{
			_transactionCancelled = true;
			if ((--_transactionDepth) == 0)
				CleanupTransaction();
		}

		// Complete the transaction
		public void CompleteTransaction()
		{
			if ((--_transactionDepth) == 0)
				CleanupTransaction();
		}

		// Helper to handle named parameters from object properties
		static Regex rxParams = new Regex(@"(?<!@)@\w+", RegexOptions.Compiled);
		public static string ProcessParams(string _sql, object[] args_src, List<object> args_dest)
		{
			return rxParams.Replace(_sql, m =>
			{
				string param = m.Value.Substring(1);

				object arg_val;

				int paramIndex;
				if (int.TryParse(param, out paramIndex))
				{
					// Numbered parameter
					if (paramIndex < 0 || paramIndex >= args_src.Length)
						throw new ArgumentOutOfRangeException(string.Format("Parameter '@{0}' specified but only {1} parameters supplied (in `{2}`)", paramIndex, args_src.Length, _sql));
					arg_val = args_src[paramIndex];
				}
				else
				{
					// Look for a property on one of the arguments with this name
					bool found = false;
					arg_val = null;
					foreach (var o in args_src)
					{
						var pi = o.GetType().GetProperty(param);
						if (pi != null)
						{
							arg_val = pi.GetValue(o, null);
							found = true;
							break;
						}
					}

					if (!found)
						throw new ArgumentException(string.Format("Parameter '@{0}' specified but none of the passed arguments have a property with this name (in '{1}')", param, _sql));
				}

				// Expand collections to parameter lists
				if ((arg_val as System.Collections.IEnumerable) != null && 
					(arg_val as string) == null && 
					(arg_val as byte[]) == null)
				{
					var sb = new StringBuilder();
					foreach (var i in arg_val as System.Collections.IEnumerable)
					{
						sb.Append((sb.Length == 0 ? "@" : ",@") + args_dest.Count.ToString());
						args_dest.Add(i);
					}
					return sb.ToString();
				}
				else
				{
					args_dest.Add(arg_val);
					return "@" + (args_dest.Count - 1).ToString();
				}
			}
			);
		}

		// Add a parameter to a DB command
		void AddParam(IDbCommand cmd, object item, string ParameterPrefix)
		{
			// Convert value to from poco type to db type
			if (Database.Mapper != null && item!=null)
			{
				var fn = Database.Mapper.GetToDbConverter(item.GetType());
				if (fn!=null)
					item = fn(item);
			}
			
			var p = cmd.CreateParameter();
			p.ParameterName = string.Format("{0}{1}", ParameterPrefix, cmd.Parameters.Count);
			if (item == null)
			{
				p.Value = DBNull.Value;
			}
			else
			{
				if (item.GetType().IsEnum)		// PostgreSQL .NET driver wont cast enum to int
				{
					p.Value = (int)item;
				}
				else if (item.GetType() == typeof(Guid))
				{
					p.Value = item.ToString();
					p.DbType = DbType.String;
					p.Size = 4000;
				}
				else if (item.GetType() == typeof(string))
				{
					p.Size = Math.Max((item as string).Length + 1, 4000);		// Help query plan caching by using common size
					p.Value = item;
				}
				else if (item.GetType() == typeof(AnsiString))
				{
					// Thanks @DataChomp for pointing out the SQL Server indexing performance hit of using wrong string type on varchar
					p.Size = Math.Max((item as string).Length + 1, 4000);
					p.Value = (item as AnsiString).Value;
					p.DbType = DbType.AnsiString;
				}
				else if (item.GetType() == typeof(bool) && _dbType != DBType.PostgreSQL)
				{
					p.Value = ((bool)item) ? 1 : 0;
				}
				else
				{
					p.Value = item;
				}
			}

			cmd.Parameters.Add(p);
		}

		// Create a command
		static Regex rxParamsPrefix = new Regex(@"(?<!@)@\w+", RegexOptions.Compiled);
		public IDbCommand CreateCommand(IDbConnection connection, string sql, params object[] args)
		{
			// Perform named argument replacements
			if (EnableNamedParams)
			{
				var new_args = new List<object>();
				sql = ProcessParams(sql, args, new_args);
				args = new_args.ToArray();
			}

			// Perform parameter prefix replacements
			if (_paramPrefix != "@")
			{
				sql = rxParamsPrefix.Replace(sql, m => _paramPrefix + m.Value.Substring(1));
				sql = sql.Replace("@@", "@");		   // <- double @@ escapes a single @
			}

			// Create the command and add parameters
			IDbCommand cmd = _factory == null ? connection.CreateCommand() : _factory.CreateCommand();
			cmd.Connection = connection;
			cmd.CommandText = sql;
			cmd.Transaction = _transaction;
			foreach (var item in args)
			{
				AddParam(cmd, item, _paramPrefix);
			}

			if (_dbType == DBType.Oracle)
			{
				cmd.GetType().GetProperty("BindByName").SetValue(cmd, true, null);
			}

			if (!String.IsNullOrEmpty(sql))
				DoPreExecute(cmd);

			return cmd;
		}

		// Override this to log/capture exceptions
		public virtual void OnException(Exception x)
		{
			System.Diagnostics.Debug.WriteLine(x.ToString());
			System.Diagnostics.Debug.WriteLine(LastCommand);
		}

		// Override this to log commands, or modify command before execution
		public virtual void OnExecutingCommand(IDbCommand cmd) { }


		// Execute a non-query command
		public int Execute(string sql, params object[] args)
		{
			try
			{
				OpenSharedConnection();
				try
				{
					using (var cmd = CreateCommand(_sharedConnection, sql, args))
					{
						return cmd.ExecuteNonQuery();
					}
				}
				finally
				{
					CloseSharedConnection();
				}
			}
			catch (Exception x)
			{
				OnException(x);
				throw;
			}
		}

		public int Execute(Sql sql)
		{
			return Execute(sql.SQL, sql.Arguments);
		}

		// Execute and cast a scalar property
		public T ExecuteScalar<T>(string sql, params object[] args)
		{
			try
			{
				OpenSharedConnection();
				try
				{
					using (var cmd = CreateCommand(_sharedConnection, sql, args))
					{
						object val = cmd.ExecuteScalar();
						return (T)Convert.ChangeType(val, typeof(T));
					}
				}
				finally
				{
					CloseSharedConnection();
				}
			}
			catch (Exception x)
			{
				OnException(x);
				throw;
			}
		}

		public T ExecuteScalar<T>(Sql sql)
		{
			return ExecuteScalar<T>(sql.SQL, sql.Arguments);
		}

		Regex rxSelect = new Regex(@"\A\s*SELECT\s", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Multiline);
		Regex rxFrom = new Regex(@"\A\s*FROM\s", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Multiline);
		string AddSelectClause<T>(string sql)
		{
			if (!rxSelect.IsMatch(sql))
			{
				var pd = PocoData.ForType(typeof(T));
				string cols = string.Join(", ", (from c in pd.QueryColumns select EscapeColumnName(c)).ToArray());
				if (!rxFrom.IsMatch(sql))
					sql = string.Format("SELECT {0} FROM {1} {2}", cols, pd.TableName, sql);
				else
					sql = string.Format("SELECT {0} {1}", cols, sql);
			}
			return sql;
		}

		public bool EnableAutoSelect { get; set; }
		public bool EnableNamedParams { get; set; }
		public bool ForceDateTimesToUtc { get; set; }

		// Return a typed list of pocos
		public List<T> Fetch<T>(string sql, params object[] args) where T : new()
		{
			return Query<T>(sql, args).ToList();
		}

		public List<T> Fetch<T>(Sql sql) where T : new()
		{
			return Fetch<T>(sql.SQL, sql.Arguments);
		}

		static Regex rxColumns = new Regex(@"\A\s*SELECT\s+((?:\((?>\((?<depth>)|\)(?<-depth>)|.?)*(?(depth)(?!))\)|.)*?)(?<!,\s+)\bFROM\b", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.Compiled);
		static Regex rxOrderBy = new Regex(@"\bORDER\s+BY\s+(?:\((?>\((?<depth>)|\)(?<-depth>)|.?)*(?(depth)(?!))\)|[\w\(\)\.])+(?:\s+(?:ASC|DESC))?(?:\s*,\s*(?:\((?>\((?<depth>)|\)(?<-depth>)|.?)*(?(depth)(?!))\)|[\w\(\)\.])+(?:\s+(?:ASC|DESC))?)*", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.Compiled);
		public static bool SplitSqlForPaging(string sql, out string sqlCount, out string sqlSelectRemoved, out string sqlOrderBy)
		{
			sqlSelectRemoved = null;
			sqlCount = null;
			sqlOrderBy = null;

			// Extract the columns from "SELECT <whatever> FROM"
			var m = rxColumns.Match(sql);
			if (!m.Success)
				return false;

			// Save column list and replace with COUNT(*)
			Group g = m.Groups[1];
			sqlCount = sql.Substring(0, g.Index) + "COUNT(*) " + sql.Substring(g.Index + g.Length);
			sqlSelectRemoved = sql.Substring(g.Index);

			// Look for an "ORDER BY <whatever>" clause
			m = rxOrderBy.Match(sqlCount);
			if (!m.Success)
				return false;

			g = m.Groups[0];
			sqlOrderBy = g.ToString();
			sqlCount = sqlCount.Substring(0, g.Index) + sqlCount.Substring(g.Index + g.Length);

			return true;
		}

		public void BuildPageQueries<T>(long page, long itemsPerPage, string sql, ref object[] args, out string sqlCount, out string sqlPage) where T : new()
		{
			// Add auto select clause
			if (EnableAutoSelect)
				sql = AddSelectClause<T>(sql);

			// Split the SQL into the bits we need
			string sqlSelectRemoved, sqlOrderBy;
			if (!SplitSqlForPaging(sql, out sqlCount, out sqlSelectRemoved, out sqlOrderBy))
				throw new Exception("Unable to parse SQL statement for paged query");

			// Build the SQL for the actual final result
			if (_dbType == DBType.SqlServer || _dbType == DBType.Oracle)
			{
				sqlSelectRemoved = rxOrderBy.Replace(sqlSelectRemoved, "");
				sqlPage = string.Format("SELECT * FROM (SELECT ROW_NUMBER() OVER ({0}) peta_rn, {1}) peta_paged WHERE peta_rn>@{2} AND peta_rn<=@{3}",
										sqlOrderBy, sqlSelectRemoved, args.Length, args.Length + 1);
				args = args.Concat(new object[] { (page - 1) * itemsPerPage, page * itemsPerPage }).ToArray();
			}
			else if (_dbType == DBType.SqlServerCE)
			{
				sqlPage = string.Format("{0}\nOFFSET @{1} ROWS FETCH NEXT @{2} ROWS ONLY", sql, args.Length, args.Length + 1);
				args = args.Concat(new object[] { (page - 1) * itemsPerPage, itemsPerPage }).ToArray();
			}
			else
			{
				sqlPage = string.Format("{0}\nLIMIT @{1} OFFSET @{2}", sql, args.Length, args.Length + 1);
				args = args.Concat(new object[] { itemsPerPage, (page - 1) * itemsPerPage }).ToArray();
			}

		}

		// Fetch a page	
		public Page<T> Page<T>(long page, long itemsPerPage, string sql, params object[] args) where T : new()
		{
			string sqlCount, sqlPage;
			BuildPageQueries<T>(page, itemsPerPage, sql, ref args, out sqlCount, out sqlPage);

			// Setup the paged result
			var result = new Page<T>();
			result.CurrentPage = page;
			result.ItemsPerPage = itemsPerPage;
			result.TotalItems = ExecuteScalar<long>(sqlCount, args);
			result.TotalPages = result.TotalItems / itemsPerPage;
			if ((result.TotalItems % itemsPerPage) != 0)
				result.TotalPages++;

			// Get the records
			result.Items = Fetch<T>(sqlPage, args);

			// Done
			return result;
		}

		public Page<T> Page<T>(long page, long itemsPerPage, Sql sql) where T : new()
		{
			return Page<T>(page, itemsPerPage, sql.SQL, sql.Arguments);
		}


		public List<T> Fetch<T>(long page, long itemsPerPage, string sql, params object[] args) where T : new()
		{
			string sqlCount, sqlPage;
			BuildPageQueries<T>(page, itemsPerPage, sql, ref args, out sqlCount, out sqlPage);
			return Fetch<T>(sqlPage, args);
		}

		public List<T> Fetch<T>(long page, long itemsPerPage, Sql sql) where T : new()
		{
			return Fetch<T>(page, itemsPerPage, sql.SQL, sql.Arguments);
		}

		// Return an enumerable collection of pocos
		public IEnumerable<T> Query<T>(string sql, params object[] args) where T : new()
		{
			if (EnableAutoSelect)
				sql = AddSelectClause<T>(sql);

			OpenSharedConnection();
			try
			{
				using (var cmd = CreateCommand(_sharedConnection, sql, args))
				{
					IDataReader r;
					var pd = PocoData.ForType(typeof(T));
					try
					{
						r = cmd.ExecuteReader();
					}
					catch (Exception x)
					{
						OnException(x);
						throw;
					}
					var factory = pd.GetFactory<T>(cmd.CommandText + "-" + _sharedConnection.ConnectionString + ForceDateTimesToUtc.ToString(), ForceDateTimesToUtc, r);
					using (r)
					{
						while (true)
						{
							T poco;
							try
							{
								if (!r.Read())
									yield break;
								poco = factory(r);
							}
							catch (Exception x)
							{
								OnException(x);
								throw;
							}

							yield return poco;
						}
					}
				}
			}
			finally
			{
				CloseSharedConnection();
			}
		}

		public IEnumerable<T> Query<T>(Sql sql) where T : new()
		{
			return Query<T>(sql.SQL, sql.Arguments);
		}

		public bool Exists<T>(object primaryKey) where T : new()
		{
			return FirstOrDefault<T>(string.Format("WHERE {0}=@0", EscapeColumnName(PocoData.ForType(typeof(T)).PrimaryKey)), primaryKey) != null;
		}
		public T Single<T>(object primaryKey) where T : new()
		{
			return Single<T>(string.Format("WHERE {0}=@0", EscapeColumnName(PocoData.ForType(typeof(T)).PrimaryKey)), primaryKey);
		}
		public T SingleOrDefault<T>(object primaryKey) where T : new()
		{
			return SingleOrDefault<T>(string.Format("WHERE {0}=@0", EscapeColumnName(PocoData.ForType(typeof(T)).PrimaryKey)), primaryKey);
		}
		public T Single<T>(string sql, params object[] args) where T : new()
		{
			return Query<T>(sql, args).Single();
		}
		public T SingleOrDefault<T>(string sql, params object[] args) where T : new()
		{
			return Query<T>(sql, args).SingleOrDefault();
		}
		public T First<T>(string sql, params object[] args) where T : new()
		{
			return Query<T>(sql, args).First();
		}
		public T FirstOrDefault<T>(string sql, params object[] args) where T : new()
		{
			return Query<T>(sql, args).FirstOrDefault();
		}

		public T Single<T>(Sql sql) where T : new()
		{
			return Query<T>(sql).Single();
		}
		public T SingleOrDefault<T>(Sql sql) where T : new()
		{
			return Query<T>(sql).SingleOrDefault();
		}
		public T First<T>(Sql sql) where T : new()
		{
			return Query<T>(sql).First();
		}
		public T FirstOrDefault<T>(Sql sql) where T : new()
		{
			return Query<T>(sql).FirstOrDefault();
		}

		public string EscapeColumnName(string str)
		{
			switch (_dbType)
			{
				case DBType.MySql:
					return string.Format("`{0}`", str);

				case DBType.PostgreSQL:
					return string.Format("\"{0}\"", str);

				default:
					return string.Format("[{0}]", str);
			}
		}

		// Insert a poco into a table.  If the poco has a property with the same name 
		// as the primary key the id of the new record is assigned to it.  Either way,
		// the new id is returned.
		public object Insert(string tableName, string primaryKeyName, object poco)
		{
			try
			{
				OpenSharedConnection();
				try
				{
					using (var cmd = CreateCommand(_sharedConnection, ""))
					{
						var pd = PocoData.ForObject(poco, primaryKeyName);
						var names = new List<string>();
						var values = new List<string>();
						var index = 0;
						foreach (var i in pd.Columns)
						{
							// Don't insert result columns
							if (i.Value.ResultColumn)
								continue;

							// Don't insert the primary key (except under oracle where we need bring in the next sequence value)
							if (primaryKeyName != null && string.Compare(i.Key, primaryKeyName, true)==0)
							{
								if (_dbType == DBType.Oracle && !string.IsNullOrEmpty(pd.SequenceName))
								{
									names.Add(i.Key);
									values.Add(string.Format("{0}.nextval", pd.SequenceName));
								}
								continue;
							}

							names.Add(EscapeColumnName(i.Key));
							values.Add(string.Format("{0}{1}", _paramPrefix, index++));
							AddParam(cmd, i.Value.GetValue(poco), _paramPrefix);
						}

						cmd.CommandText = string.Format("INSERT INTO {0} ({1}) VALUES ({2})",
								tableName,
								string.Join(",", names.ToArray()),
								string.Join(",", values.ToArray())
								);


						object id;
						switch (_dbType)
						{
							case DBType.SqlServerCE:
								DoPreExecute(cmd);
								cmd.ExecuteNonQuery();
								id = ExecuteScalar<object>("SELECT @@IDENTITY AS NewID;");
								break;
							case DBType.SqlServer:
								cmd.CommandText += ";\nSELECT SCOPE_IDENTITY() AS NewID;";
								DoPreExecute(cmd);
								id = cmd.ExecuteScalar();
								break;
							case DBType.PostgreSQL:
								if (primaryKeyName != null)
								{
									cmd.CommandText += string.Format("returning {0} as NewID", EscapeColumnName(primaryKeyName));
									DoPreExecute(cmd);
									id = cmd.ExecuteScalar();
								}
								else
								{
									id = -1;
									DoPreExecute(cmd);
									cmd.ExecuteNonQuery();
								}
								break;
							case DBType.Oracle:
								if (primaryKeyName != null)
								{
									cmd.CommandText += string.Format(" returning {0} into :newid", EscapeColumnName(primaryKeyName));
									var param = cmd.CreateParameter();
									param.ParameterName = ":newid";
									param.Value = DBNull.Value;
									param.Direction = ParameterDirection.ReturnValue;
									param.DbType = DbType.Int64;
									cmd.Parameters.Add(param);
									DoPreExecute(cmd);
									cmd.ExecuteNonQuery();
									id = param.Value;
								}
								else
								{
									id = -1;
									DoPreExecute(cmd);
									cmd.ExecuteNonQuery();
								}
								break;
							default:
								cmd.CommandText += ";\nSELECT @@IDENTITY AS NewID;";
								DoPreExecute(cmd);
								id = cmd.ExecuteScalar();
								break;
						}


						// Assign the ID back to the primary key property
						if (primaryKeyName != null)
						{
							PocoColumn pc;
							if (pd.Columns.TryGetValue(primaryKeyName, out pc))
							{
								pc.SetValue(poco, pc.ChangeType(id));
							}
						}

						return id;
					}
				}
				finally
				{
					CloseSharedConnection();
				}
			}
			catch (Exception x)
			{
				OnException(x);
				throw;
			}
		}

		// Insert an annotated poco object
		public object Insert(object poco)
		{
			var pd = PocoData.ForType(poco.GetType());
			return Insert(pd.TableName, pd.PrimaryKey, poco);
		}

		// Update a record with values from a poco.  primary key value can be either supplied or read from the poco
		public int Update(string tableName, string primaryKeyName, object poco, object primaryKeyValue)
		{
			try
			{
				OpenSharedConnection();
				try
				{
					using (var cmd = CreateCommand(_sharedConnection, ""))
					{
						var sb = new StringBuilder();
						var index = 0;
						var pd = PocoData.ForObject(poco,primaryKeyName);
						foreach (var i in pd.Columns)
						{
							// Don't update the primary key, but grab the value if we don't have it
							if (string.Compare(i.Key, primaryKeyName, true)==0)
							{
								if (primaryKeyValue == null)
									primaryKeyValue = i.Value.GetValue(poco);
								continue;
							}

							// Dont update result only columns
							if (i.Value.ResultColumn)
								continue;

							// Build the sql
							if (index > 0)
								sb.Append(", ");
							sb.AppendFormat("{0} = {1}{2}", EscapeColumnName(i.Key), _paramPrefix, index++);

							// Store the parameter in the command
							AddParam(cmd, i.Value.GetValue(poco), _paramPrefix);
						}

						cmd.CommandText = string.Format("UPDATE {0} SET {1} WHERE {2} = {3}{4}",
											tableName, sb.ToString(), EscapeColumnName(primaryKeyName), _paramPrefix, index++);
						AddParam(cmd, primaryKeyValue, _paramPrefix);

						DoPreExecute(cmd);

						// Do it
						return cmd.ExecuteNonQuery();
					}
				}
				finally
				{
					CloseSharedConnection();
				}
			}
			catch (Exception x)
			{
				OnException(x);
				throw;
			}
		}

		public int Update(string tableName, string primaryKeyName, object poco)
		{
			return Update(tableName, primaryKeyName, poco, null);
		}

		public int Update(object poco)
		{
			return Update(poco, null);
		}

		public int Update(object poco, object primaryKeyValue)
		{
			var pd = PocoData.ForType(poco.GetType());
			return Update(pd.TableName, pd.PrimaryKey, poco, primaryKeyValue);
		}

		public int Update<T>(string sql, params object[] args)
		{
			var pd = PocoData.ForType(typeof(T));
			return Execute(string.Format("UPDATE {0} {1}", pd.TableName, sql), args);
		}

		public int Update<T>(Sql sql)
		{
			var pd = PocoData.ForType(typeof(T));
			return Execute(new Sql(string.Format("UPDATE {0}", pd.TableName)).Append(sql));
		}

		public int Delete(string tableName, string primaryKeyName, object poco)
		{
			return Delete(tableName, primaryKeyName, poco, null);
		}

		public int Delete(string tableName, string primaryKeyName, object poco, object primaryKeyValue)
		{
			// If primary key value not specified, pick it up from the object
			if (primaryKeyValue == null)
			{
				var pd = PocoData.ForObject(poco,primaryKeyName);
				PocoColumn pc;
				if (pd.Columns.TryGetValue(primaryKeyName, out pc))
				{
					primaryKeyValue = pc.GetValue(poco);
				}
			}

			// Do it
			var sql = string.Format("DELETE FROM {0} WHERE {1}=@0", tableName, EscapeColumnName(primaryKeyName));
			return Execute(sql, primaryKeyValue);
		}

		public int Delete(object poco)
		{
			var pd = PocoData.ForType(poco.GetType());
			return Delete(pd.TableName, pd.PrimaryKey, poco);
		}

		public int Delete<T>(object pocoOrPrimaryKey)
		{
			if (pocoOrPrimaryKey.GetType() == typeof(T))
				return Delete(pocoOrPrimaryKey);
			var pd = PocoData.ForType(typeof(T));
			return Delete(pd.TableName, pd.PrimaryKey, null, pocoOrPrimaryKey);
		}

		public int Delete<T>(string sql, params object[] args)
		{
			var pd = PocoData.ForType(typeof(T));
			return Execute(string.Format("DELETE FROM {0} {1}", pd.TableName, sql), args);
		}

		public int Delete<T>(Sql sql)
		{
			var pd = PocoData.ForType(typeof(T));
			return Execute(new Sql(string.Format("DELETE FROM {0}", pd.TableName)).Append(sql));
		}

		// Check if a poco represents a new record
		public bool IsNew(string primaryKeyName, object poco)
		{
			// If primary key value not specified, pick it up from the object
			var pd = PocoData.ForObject(poco, primaryKeyName);
			object pk;
			PocoColumn pc;
			if (pd.Columns.TryGetValue(primaryKeyName, out pc))
			{
				pk = pc.GetValue(poco);
			}
#if !PETAPOCO_NO_DYNAMIC
			else if (poco.GetType() == typeof(System.Dynamic.ExpandoObject))
			{
				return true;
			}
#endif
			else
			{
				var pi = poco.GetType().GetProperty(primaryKeyName);
				if (pi == null)
					throw new ArgumentException(string.Format("The object doesn't have a property matching the primary key column name '{0}'", primaryKeyName));
				pk = pi.GetValue(poco, null);
			}

			if (pk == null)
				return true;

			var type = pk.GetType();

			if (type.IsValueType)
			{
				// Common primary key types
				if (type == typeof(long))
					return (long)pk == 0;
				else if (type == typeof(ulong))
					return (ulong)pk == 0;
				else if (type == typeof(int))
					return (int)pk == 0;
				else if (type == typeof(uint))
					return (int)pk == 0;

				// Create a default instance and compare
				return pk == Activator.CreateInstance(pk.GetType());
			}
			else
			{
				return pk == null;
			}
		}

		public bool IsNew(object poco)
		{
			var pd = PocoData.ForType(poco.GetType());
			return IsNew(pd.PrimaryKey, poco);
		}

		// Insert new record or Update existing record
		public void Save(string tableName, string primaryKeyName, object poco)
		{
			if (IsNew(primaryKeyName, poco))
			{
				Insert(tableName, primaryKeyName, poco);
			}
			else
			{
				Update(tableName, primaryKeyName, poco);
			}
		}

		public void Save(object poco)
		{
			var pd = PocoData.ForType(poco.GetType());
			Save(pd.TableName, pd.PrimaryKey, poco);
		}

		void DoPreExecute(IDbCommand cmd)
		{
			OnExecutingCommand(cmd);
			_lastSql = cmd.CommandText;
			_lastArgs = (from IDataParameter parameter in cmd.Parameters select parameter.Value).ToArray();
		}

		public string LastSQL { get { return _lastSql; } }
		public object[] LastArgs { get { return _lastArgs; } }
		public string LastCommand
		{
			get
			{
				var sb = new StringBuilder();
				if (_lastSql == null)
					return "";
				sb.Append(_lastSql);
				if (_lastArgs != null && _lastArgs.Length > 0)
				{
					sb.Append("\r\n\r\n");
					for (int i = 0; i < _lastArgs.Length; i++)
					{
						sb.AppendFormat("{0}{1} [{2}] = \"{3}\"\r\n", _paramPrefix, i, _lastArgs[i].GetType().Name, _lastArgs[i]);
					}
				}
				return sb.ToString();
			}
		}

		public static IMapper Mapper
		{
			get;
			set;
		}

		internal class PocoColumn
		{
			public string ColumnName;
			public PropertyInfo PropertyInfo;
			public bool ResultColumn;
			public virtual void SetValue(object target, object val) { PropertyInfo.SetValue(target, val, null); }
			public virtual object GetValue(object target) { return PropertyInfo.GetValue(target, null); }
			public virtual object ChangeType(object val) { return Convert.ChangeType(val, PropertyInfo.PropertyType); }
		}
		internal class ExpandoColumn : PocoColumn
		{
			public override void SetValue(object target, object val) { (target as IDictionary<string, object>)[ColumnName]=val; }
			public override object GetValue(object target) 
			{ 
				object val=null;
				(target as IDictionary<string, object>).TryGetValue(ColumnName, out val);
				return val;
			}
			public override object ChangeType(object val) { return val; }
		}
		internal class PocoData
		{
			public static PocoData ForObject(object o, string primaryKeyName)
			{
				var t = o.GetType();
#if !PETAPOCO_NO_DYNAMIC
				if (t == typeof(System.Dynamic.ExpandoObject))
				{
					var pd = new PocoData();
					pd.Columns = new Dictionary<string, PocoColumn>(StringComparer.OrdinalIgnoreCase);
					pd.Columns.Add(primaryKeyName, new ExpandoColumn() { ColumnName = primaryKeyName });
					pd.PrimaryKey = primaryKeyName;
					foreach (var col in (o as IDictionary<string, object>).Keys)
					{
						if (col!=primaryKeyName)
							pd.Columns.Add(col, new ExpandoColumn() { ColumnName = col });
					}
					return pd;
				}
				else
#endif
					return ForType(t);
			}
			public static PocoData ForType(Type t)
			{
#if !PETAPOCO_NO_DYNAMIC
				if (t == typeof(System.Dynamic.ExpandoObject))
					throw new InvalidOperationException("Can't use dynamic types with this method");
#endif
				lock (m_PocoDatas)
				{
					PocoData pd;
					if (!m_PocoDatas.TryGetValue(t, out pd))
					{
						pd = new PocoData(t);
						m_PocoDatas.Add(t, pd);
					}
					return pd;
				}
			}

			public PocoData()
			{
			}

			public PocoData(Type t)
			{
				// Get the table name
				var a = t.GetCustomAttributes(typeof(TableNameAttribute), true);
				var tempTableName = a.Length == 0 ? t.Name : (a[0] as TableNameAttribute).Value;

				// Get the primary key
				a = t.GetCustomAttributes(typeof(PrimaryKeyAttribute), true);
				var tempPrimaryKey = a.Length == 0 ? "ID" : (a[0] as PrimaryKeyAttribute).Value;
				var tempSequenceName = a.Length == 0 ? null : (a[0] as PrimaryKeyAttribute).sequenceName;

				// Call column mapper
				if (Database.Mapper != null)
					Database.Mapper.GetTableInfo(t, ref tempTableName, ref tempPrimaryKey, ref tempSequenceName);
				TableName = tempTableName;
				PrimaryKey = tempPrimaryKey;
				SequenceName = tempSequenceName;

				// Work out bound properties
				bool ExplicitColumns = t.GetCustomAttributes(typeof(ExplicitColumnsAttribute), true).Length > 0;
				Columns = new Dictionary<string, PocoColumn>(StringComparer.OrdinalIgnoreCase);
				foreach (var pi in t.GetProperties())
				{
					// Work out if properties is to be included
					var ColAttrs = pi.GetCustomAttributes(typeof(ColumnAttribute), true);
					if (ExplicitColumns)
					{
						if (ColAttrs.Length == 0)
							continue;
					}
					else
					{
						if (pi.GetCustomAttributes(typeof(IgnoreAttribute), true).Length != 0)
							continue;
					}

					var pc = new PocoColumn();
					pc.PropertyInfo = pi;

					// Work out the DB column name
					if (ColAttrs.Length > 0)
					{
						var colattr = (ColumnAttribute)ColAttrs[0];
						pc.ColumnName = colattr.Name;
						if ((colattr as ResultColumnAttribute) != null)
							pc.ResultColumn = true;
					}
					if (pc.ColumnName == null)
					{
						pc.ColumnName = pi.Name;
						if (Database.Mapper != null && !Database.Mapper.MapPropertyToColumn(pi, ref pc.ColumnName, ref pc.ResultColumn))
							continue;
					}

					// Store it
					Columns.Add(pc.ColumnName, pc);
				}

				// Build column list for automatic select
				QueryColumns = (from c in Columns where !c.Value.ResultColumn select c.Key).ToArray();

			}

			bool IsIntegralType(Type t)
			{
				var tc=Type.GetTypeCode(t);
				return tc >= TypeCode.SByte && tc <= TypeCode.UInt64;
			}

			// Create factory function that can convert a IDataReader record into a POCO
			public Func<IDataReader, T> GetFactory<T>(string key, bool ForceDateTimesToUtc, IDataReader r)
			{
				lock (PocoFactories)
				{
					// Have we already created it?
					object factory;
					if (PocoFactories.TryGetValue(key, out factory))
						return factory as Func<IDataReader, T>;

					lock (m_Converters)
					{
						// Create the method
						var m = new DynamicMethod("petapoco_factory_" + PocoFactories.Count.ToString(), typeof(T), new Type[] { typeof(IDataReader) }, true);
						var il = m.GetILGenerator();

#if !PETAPOCO_NO_DYNAMIC
						if (typeof(T) == typeof(object))
						{
							// var poco=new T()
							il.Emit(OpCodes.Newobj, typeof(System.Dynamic.ExpandoObject).GetConstructor(Type.EmptyTypes));			// obj

							MethodInfo fnAdd = typeof(IDictionary<string, object>).GetMethod("Add");

							// Enumerate all fields generating a set assignment for the column
							for (int i = 0; i < r.FieldCount; i++)
							{
								var srcType = r.GetFieldType(i);

								il.Emit(OpCodes.Dup);						// obj, obj
								il.Emit(OpCodes.Ldstr, r.GetName(i));		// obj, obj, fieldname

								// Get the converter
								Func<object, object> converter = null;
								if (Database.Mapper != null)
									converter = Database.Mapper.GetFromDbConverter(null, srcType);
								if (ForceDateTimesToUtc && converter == null && srcType == typeof(DateTime))
									converter = delegate(object src) { return new DateTime(((DateTime)src).Ticks, DateTimeKind.Utc); };

								// Setup stack for call to converter
								int converterIndex = -1;
								if (converter != null)
								{
									// Add the converter
									converterIndex = m_Converters.Count;
									m_Converters.Add(converter);

									// Generate IL to push the converter onto the stack
									il.Emit(OpCodes.Ldsfld, fldConverters);
									il.Emit(OpCodes.Ldc_I4, converterIndex);
									il.Emit(OpCodes.Callvirt, fnListGetItem);					// obj, obj, fieldname, Converter
								}

		
								// r[i]
								il.Emit(OpCodes.Ldarg_0);					// obj, obj, fieldname, converter?,    rdr
								il.Emit(OpCodes.Ldc_I4, i);					// obj, obj, fieldname, converter?,  rdr,i
								il.Emit(OpCodes.Callvirt, fnGetValue);		// obj, obj, fieldname, converter?,  value

								// Convert DBNull to null
								il.Emit(OpCodes.Dup);						// obj, obj, fieldname, converter?,  value, value
								il.Emit(OpCodes.Isinst, typeof(DBNull));	// obj, obj, fieldname, converter?,  value, (value or null)
								var lblNotNull = il.DefineLabel();
								il.Emit(OpCodes.Brfalse_S, lblNotNull);		// obj, obj, fieldname, converter?,  value
								il.Emit(OpCodes.Pop);						// obj, obj, fieldname, converter?
								if (converter!=null)
									il.Emit(OpCodes.Pop);					// obj, obj, fieldname, 
								il.Emit(OpCodes.Ldnull);					// obj, obj, fieldname, null
								if (converter != null)
								{
									var lblReady = il.DefineLabel();
									il.Emit(OpCodes.Br_S, lblReady);
									il.MarkLabel(lblNotNull);
									il.Emit(OpCodes.Callvirt, fnInvoke);
									il.MarkLabel(lblReady);
								}
								else
								{
									il.MarkLabel(lblNotNull);
								}

								il.Emit(OpCodes.Callvirt, fnAdd);
							}
						}
						else
#endif
						{
							// var poco=new T()
							il.Emit(OpCodes.Newobj, typeof(T).GetConstructor(Type.EmptyTypes));

							// Enumerate all fields generating a set assignment for the column
							for (int i = 0; i < r.FieldCount; i++)
							{
								// Get the PocoColumn for this db column, ignore if not known
								PocoColumn pc;
								if (!Columns.TryGetValue(r.GetName(i), out pc))
									continue;

								// Get the source type for this column
								var srcType = r.GetFieldType(i);
								var dstType = pc.PropertyInfo.PropertyType;

								// "if (!rdr.IsDBNull(i))"
								il.Emit(OpCodes.Ldarg_0);										// poco,rdr
								il.Emit(OpCodes.Ldc_I4, i);										// poco,rdr,i
								il.Emit(OpCodes.Callvirt, fnIsDBNull);							// poco,bool
								var lblNext = il.DefineLabel();
								il.Emit(OpCodes.Brtrue_S, lblNext);								// poco

								il.Emit(OpCodes.Dup);											// poco,poco

								// Do we need to install a converter?
								Func<object, object> converter = null;

								// Get converter from the mapper
								if (Database.Mapper != null)
								{
									converter = Database.Mapper.GetFromDbConverter(pc.PropertyInfo, srcType);
								}

								// Standard DateTime->Utc mapper
								if (ForceDateTimesToUtc && converter == null && srcType == typeof(DateTime) && (dstType == typeof(DateTime) || dstType == typeof(DateTime?)))
								{
									converter = delegate(object src) { return new DateTime(((DateTime)src).Ticks, DateTimeKind.Utc); };
								}

								// Forced type conversion including integral types -> enum
								if (converter == null)
								{
									if (dstType.IsEnum && IsIntegralType(srcType))
									{
										if (srcType != typeof(int))
										{
											converter = delegate(object src) { return Convert.ChangeType(src, typeof(int), null); };
										}
									}
									else if (!dstType.IsAssignableFrom(srcType))
									{
										converter = delegate(object src) { return Convert.ChangeType(src, dstType, null); };
									}
								}

								// Fast
								bool Handled = false;
								if (converter == null)
								{
									var valuegetter = typeof(IDataRecord).GetMethod("Get" + srcType.Name, new Type[] { typeof(int) });
									if (valuegetter != null
											&& valuegetter.ReturnType == srcType
											&& (valuegetter.ReturnType == dstType || valuegetter.ReturnType == Nullable.GetUnderlyingType(dstType)))
									{
										il.Emit(OpCodes.Ldarg_0);										// *,rdr
										il.Emit(OpCodes.Ldc_I4, i);										// *,rdr,i
										il.Emit(OpCodes.Callvirt, valuegetter);							// *,value

										// Convert to Nullable
										if (Nullable.GetUnderlyingType(dstType) != null)
										{
											il.Emit(OpCodes.Newobj, dstType.GetConstructor(new Type[] { Nullable.GetUnderlyingType(dstType) }));
										}

										il.Emit(OpCodes.Callvirt, pc.PropertyInfo.GetSetMethod());		// poco
										Handled = true;
									}
								}

								// Not so fast
								if (!Handled)
								{
									// Setup stack for call to converter
									int converterIndex = -1;
									if (converter != null)
									{
										// Add the converter
										converterIndex = m_Converters.Count;
										m_Converters.Add(converter);

										// Generate IL to push the converter onto the stack
										il.Emit(OpCodes.Ldsfld, fldConverters);
										il.Emit(OpCodes.Ldc_I4, converterIndex);
										il.Emit(OpCodes.Callvirt, fnListGetItem);					// Converter
									}

									// "value = rdr.GetValue(i)"
									il.Emit(OpCodes.Ldarg_0);										// *,rdr
									il.Emit(OpCodes.Ldc_I4, i);										// *,rdr,i
									il.Emit(OpCodes.Callvirt, fnGetValue);							// *,value

									// Call the converter
									if (converter != null)
										il.Emit(OpCodes.Callvirt, fnInvoke);

									// Assign it
									il.Emit(OpCodes.Unbox_Any, pc.PropertyInfo.PropertyType);		// poco,poco,value
									il.Emit(OpCodes.Callvirt, pc.PropertyInfo.GetSetMethod());		// poco
								}

								il.MarkLabel(lblNext);
							}
						}

						il.Emit(OpCodes.Ret);

						// Cache it, return it
						var del = (Func<IDataReader, T>)m.CreateDelegate(typeof(Func<IDataReader, T>));
						PocoFactories.Add(key, del);
						return del;
					}
				}
			}


			static Dictionary<Type, PocoData> m_PocoDatas = new Dictionary<Type, PocoData>();
			static List<Func<object, object>> m_Converters = new List<Func<object, object>>();
			static MethodInfo fnGetValue = typeof(IDataRecord).GetMethod("GetValue", new Type[] { typeof(int) });
			static MethodInfo fnIsDBNull = typeof(IDataRecord).GetMethod("IsDBNull");
			static FieldInfo fldConverters = typeof(PocoData).GetField("m_Converters", BindingFlags.Static | BindingFlags.GetField | BindingFlags.NonPublic);
			static MethodInfo fnListGetItem = typeof(List<Func<object, object>>).GetProperty("Item").GetGetMethod();
			static MethodInfo fnInvoke = typeof(Func<object, object>).GetMethod("Invoke");
			public string TableName { get; private set; }
			public string PrimaryKey { get; private set; }
			public string SequenceName { get; private set; }
			public string[] QueryColumns { get; private set; }
			public Dictionary<string, PocoColumn> Columns { get; private set; }
			Dictionary<string, object> PocoFactories = new Dictionary<string, object>();
		}


		// Member variables
		string _connectionString;
		string _providerName;
		DbProviderFactory _factory;
		IDbConnection _sharedConnection;
		IDbTransaction _transaction;
		int _sharedConnectionDepth;
		int _transactionDepth;
		bool _transactionCancelled;
		string _lastSql;
		object[] _lastArgs;
		string _paramPrefix = "@";
	}

	// Transaction object helps maintain transaction depth counts
	public class Transaction : IDisposable
	{
		public Transaction(Database db)
		{
			_db = db;
			_db.BeginTransaction();
		}

		public void Complete()
		{
			_db.CompleteTransaction();
			_db = null;
		}

		public void Dispose()
		{
			if (_db != null)
				_db.AbortTransaction();
		}

		Database _db;
	}

	// Simple helper class for building SQL statments
	public class Sql
	{
		public Sql()
		{
		}

		public Sql(string sql, params object[] args)
		{
			_sql = sql;
			_args = args;
		}

		public static Sql Builder
		{
			get { return new Sql(); }
		}

		string _sql;
		object[] _args;
		Sql _rhs;
		string _sqlFinal;
		object[] _argsFinal;

		private void Build()
		{
			// already built?
			if (_sqlFinal != null)
				return;

			// Build it
			var sb = new StringBuilder();
			var args = new List<object>();
			Build(sb, args, null);
			_sqlFinal = sb.ToString();
			_argsFinal = args.ToArray();
		}

		public string SQL
		{
			get
			{
				Build();
				return _sqlFinal;
			}
		}

		public object[] Arguments
		{
			get
			{
				Build();
				return _argsFinal;
			}
		}

		public Sql Append(Sql sql)
		{
			if (_rhs != null)
				_rhs.Append(sql);
			else
				_rhs = sql;

			return this;
		}

		public Sql Append(string sql, params object[] args)
		{
			return Append(new Sql(sql, args));
		}

		static bool Is(Sql sql, string sqltype)
		{
			return sql != null && sql._sql != null && sql._sql.StartsWith(sqltype, StringComparison.InvariantCultureIgnoreCase);
		}

		private void Build(StringBuilder sb, List<object> args, Sql lhs)
		{
			if (!String.IsNullOrEmpty(_sql))
			{
				// Add SQL to the string
				if (sb.Length > 0)
				{
					sb.Append("\n");
				}

				var sql = Database.ProcessParams(_sql, _args, args);

				if (Is(lhs, "WHERE ") && Is(this, "WHERE "))
					sql = "AND " + sql.Substring(6);
				if (Is(lhs, "ORDER BY ") && Is(this, "ORDER BY "))
					sql = ", " + sql.Substring(9);

				sb.Append(sql);
			}

			// Now do rhs
			if (_rhs != null)
				_rhs.Build(sb, args, this);
		}

		public Sql Where(string sql, params object[] args)
		{
			return Append(new Sql("WHERE " + sql, args));
		}

		public Sql OrderBy(params object[] columns)
		{
			return Append(new Sql("ORDER BY " + String.Join(", ", (from x in columns select x.ToString()).ToArray())));
		}

		public Sql Select(params object[] columns)
		{
			return Append(new Sql("SELECT " + String.Join(", ", (from x in columns select x.ToString()).ToArray())));
		}

		public Sql From(params object[] tables)
		{
			return Append(new Sql("FROM " + String.Join(", ", (from x in tables select x.ToString()).ToArray())));
		}

		public Sql GroupBy(params object[] columns)
		{
			return Append(new Sql("GROUP BY " + String.Join(", ", (from x in columns select x.ToString()).ToArray())));
		}

		private SqlJoinClause Join(string JoinType, string table)
		{
			return new SqlJoinClause(Append(new Sql(JoinType + table)));
		}

		public SqlJoinClause InnerJoin(string table) { return Join("INNER JOIN ", table); }
		public SqlJoinClause LeftJoin(string table) { return Join("LEFT JOIN ", table); }

		public class SqlJoinClause
		{
			private readonly Sql _sql;

			public SqlJoinClause(Sql sql)
			{
				_sql = sql;
			}

			public Sql On(string onClause, params object[] args)
			{
				return _sql.Append("ON " + onClause, args);
			}
		}
	}

}
