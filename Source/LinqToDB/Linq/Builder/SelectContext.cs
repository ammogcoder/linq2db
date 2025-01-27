﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace LinqToDB.Linq.Builder
{
	using LinqToDB.Expressions;
	using Extensions;
	using SqlQuery;
	using Common;

	// This class implements double functionality (scalar and member type selects)
	// and could be implemented as two different classes.
	// But the class means to have a lot of inheritors, and functionality of the inheritors
	// will be doubled as well. So lets double it once here.
	//
	class SelectContext : IBuildContext
	{
		#region Init

#if DEBUG
		public string _sqlQueryText => SelectQuery == null ? "" : SelectQuery.SqlText;
		public string Path => this.GetPath();
		public MethodCallExpression MethodCall;
#endif

		public IBuildContext[]   Sequence    { [DebuggerStepThrough] get; }
		public LambdaExpression  Lambda      { [DebuggerStepThrough] get; set; }
		public Expression        Body        { [DebuggerStepThrough] get; set; }
		public ExpressionBuilder Builder     { [DebuggerStepThrough] get; }
		public SelectQuery       SelectQuery { [DebuggerStepThrough] get; set; }
		public SqlStatement      Statement   { [DebuggerStepThrough] get; set; }
		public IBuildContext     Parent      { [DebuggerStepThrough] get; set; }
		public bool              IsScalar    { [DebuggerStepThrough] get; }

		Expression IBuildContext.Expression => Lambda;

		public readonly Dictionary<MemberInfo,Expression> Members = new Dictionary<MemberInfo,Expression>(new MemberInfoComparer());

		public SelectContext(IBuildContext parent, LambdaExpression lambda, params IBuildContext[] sequences)
		{
			Parent      = parent;
			Sequence    = sequences;
			Builder     = sequences[0].Builder;
			Lambda      = lambda;
			Body        = lambda.Body;
			SelectQuery = sequences[0].SelectQuery;

			foreach (var context in Sequence)
				context.Parent = this;

			IsScalar = !Builder.ProcessProjection(Members, Body);

			Builder.Contexts.Add(this);
		}

		#endregion

		#region BuildQuery

		public virtual void BuildQuery<T>(Query<T> query, ParameterExpression queryParameter)
		{
			var expr   = BuildExpression(null, 0, false);
			var mapper = Builder.BuildMapper<T>(expr);

			QueryRunner.SetRunQuery(query, mapper);
		}

		#endregion

		#region BuildExpression

		ParameterExpression _rootExpression;

		public virtual Expression BuildExpression(Expression expression, int level, bool enforceServerSide)
#if DEBUG && TRACK_BUILD
		{
			Debug.WriteLine("{0}.BuildExpression start {1}:\n{2}".Args(GetType().Name, level, (expression ?? Body).GetDebugView()));
			Debug.WriteLine("{0}.BuildExpression start:\n{1}".Args(GetType().Name, SelectQuery));

			var expr = BuildExpressionInternal(expression, level, enforceServerSide);

			Debug.WriteLine("{0}.BuildExpression end:\n{1}".Args(GetType().Name, (expression ?? Body).GetDebugView()));
			Debug.WriteLine("{0}.BuildExpression end:\n{1}".Args(GetType().Name, SelectQuery));

			return expr;
		}

		Expression BuildExpressionInternal(Expression expression, int level, bool enforceServerSide)
#endif
		{
			{
				var key = Tuple.Create(expression, level, ConvertFlags.Field);

				if (_expressionIndex.TryGetValue(key, out var info))
				{
					var idx  = Parent?.ConvertToParentIndex(info[0].Index, this) ?? info[0].Index;

					var expr = expression ?? Body;

					if (IsExpression(expr, level, RequestFor.Object).Result)
						return Builder.BuildExpression(this, expr, enforceServerSide);

					return Builder.BuildSql(expr.Type, idx);
				}
			}

			if (expression == null)
			{
				if (_rootExpression == null)
				{
					var expr = Builder.BuildExpression(this, Body, enforceServerSide);

					if (Builder.IsBlockDisable)
						return expr;

					_rootExpression = Builder.BuildVariable(expr);
				}

				return _rootExpression;
			}

			var levelExpression = expression.GetLevelExpression(Builder.MappingSchema, level);

			if (IsScalar)
			{
				if (Body.NodeType != ExpressionType.Parameter && level == 0)
					if (ReferenceEquals(levelExpression, expression))
						if (IsSubQuery() && IsExpression(null, 0, RequestFor.Expression).Result)
						{
							var info = ConvertToIndex(expression, level, ConvertFlags.Field).Single();
							var idx = Parent?.ConvertToParentIndex(info.Index, this) ?? info.Index;

							return Builder.BuildSql(expression.Type, idx);
						}

				return ProcessScalar(
					expression,
					level,
					(ctx, ex, l) => ctx.BuildExpression(ex, l, enforceServerSide),
					() => GetSequence(expression, level).BuildExpression(null, 0, enforceServerSide));
			}
			else
			{
				if (level == 0)
				{
					var sequence = GetSequence(expression, level);

					return ReferenceEquals(levelExpression, expression) ?
						sequence.BuildExpression(null,       0,         enforceServerSide) :
						sequence.BuildExpression(expression, level + 1, enforceServerSide);
				}

				switch (levelExpression.NodeType)
				{
					case ExpressionType.MemberAccess :
						{
							var memberExpression = GetMemberExpression(
								((MemberExpression)levelExpression).Member,
								ReferenceEquals(levelExpression, expression),
								levelExpression.Type,
								expression);

							if (ReferenceEquals(levelExpression, expression))
							{
								if (IsSubQuery())
								{
									switch (memberExpression.NodeType)
									{
										case ExpressionType.New        :
										case ExpressionType.MemberInit :
											{
												var resultExpression = memberExpression.Transform(e =>
												{
													if (!ReferenceEquals(e, memberExpression))
													{
														switch (e.NodeType)
														{
															case ExpressionType.MemberAccess :
															case ExpressionType.Parameter :
																{
																	var sequence = GetSequence(e, 0);
																	return Builder.BuildExpression(sequence, e, enforceServerSide);
																}
														}

														if (enforceServerSide)
															return Builder.BuildExpression(this, e, true);
													}

													return e;
												});

												return resultExpression;
											}
									}

									var me = memberExpression.NodeType == ExpressionType.Parameter ? null : memberExpression;

									if (!IsExpression(me, 0, RequestFor.Object).Result &&
										!IsExpression(me, 0, RequestFor.Field). Result)
									{
										var info = ConvertToIndex(expression, level, ConvertFlags.Field).Single();
										var idx  = Parent?.ConvertToParentIndex(info.Index, this) ?? info.Index;

										return Builder.BuildSql(expression.Type, idx);
									}
								}

								return Builder.BuildExpression(this, memberExpression, enforceServerSide);
							}

							{
								var sequence = GetSequence(expression, level);

								switch (memberExpression.NodeType)
								{
									case ExpressionType.Parameter  :
										{
											var parameter = Lambda.Parameters[Sequence.Length == 0 ? 0 : Array.IndexOf(Sequence, sequence)];

											if (ReferenceEquals(memberExpression, parameter))
												return sequence.BuildExpression(expression, level + 1, enforceServerSide);

											break;
										}

									case ExpressionType.New        :
									case ExpressionType.MemberInit :
										{
											var mmExpresion = GetMemberExpression(memberExpression, expression, level + 1);
											return Builder.BuildExpression(this, mmExpresion, enforceServerSide);
										}
								}

								var expr = expression.Transform(ex => ReferenceEquals(ex, levelExpression) ? memberExpression : ex);

								if (sequence == null)
									return Builder.BuildExpression(this, expr, enforceServerSide);

								return sequence.BuildExpression(expr, 1, enforceServerSide);
							}
						}

					case ExpressionType.Parameter :
						break;
				}
			}

			throw new NotImplementedException();
		}

		#endregion

		#region ConvertToSql

		readonly Dictionary<MemberInfo,SqlInfo[]> _sql = new Dictionary<MemberInfo,SqlInfo[]>(new MemberInfoComparer());

		public virtual SqlInfo[] ConvertToSql(Expression expression, int level, ConvertFlags flags)
		{
			if (expression != null && level > 0 && expression.NodeType == ExpressionType.Call)
			{
				var e = (MethodCallExpression)expression;

				if (e.Method.DeclaringType == typeof(Enumerable))
				{
					return new[] { new SqlInfo { Sql = Builder.SubQueryToSql(this, e) } };
				}
			}

			if (IsScalar)
			{
				if (expression == null)
					return Builder.ConvertExpressions(this, Body, flags);

				switch (flags)
				{
					case ConvertFlags.Field :
					case ConvertFlags.Key   :
					case ConvertFlags.All   :
						{
							if (Body.NodeType != ExpressionType.Parameter && level == 0)
							{
								var levelExpression = expression.GetLevelExpression(Builder.MappingSchema, level);

								if (levelExpression != expression)
									if (flags != ConvertFlags.Field && IsExpression(expression, level, RequestFor.Field).Result)
										flags = ConvertFlags.Field;
							}

							return ProcessScalar(
								expression,
								level,
								(ctx, ex, l) => ctx.ConvertToSql(ex, l, flags),
								() => new[] { new SqlInfo { Sql = Builder.ConvertToSql(this, expression) } });
						}
				}
			}
			else
			{
				if (expression == null)
				{
					if (flags != ConvertFlags.Field)
					{
						var q =
							from m in Members
							where !(m.Key is MethodInfo)
							select ConvertMember(m.Key, m.Value, flags) into mm
							from m in mm
							select m;

						return q.ToArray();
					}

					throw new NotImplementedException();
				}

				switch (flags)
				{
					case ConvertFlags.All   :
					case ConvertFlags.Key   :
					case ConvertFlags.Field :
						{
							var levelExpression = expression.GetLevelExpression(Builder.MappingSchema, level);

							switch (levelExpression.NodeType)
							{
								case ExpressionType.MemberAccess :
									{
										if (level != 0 && levelExpression == expression)
										{
											var member = ((MemberExpression)levelExpression).Member;

											if (!_sql.TryGetValue(member, out var sql))
											{
												var memberExpression = GetMemberExpression(
													member, levelExpression == expression, levelExpression.Type, expression);

												sql = ConvertExpressions(memberExpression, flags)
													.Select(si => si.Clone(member)).ToArray();

												_sql.Add(member, sql);
											}

											return sql;
										}

										return ProcessMemberAccess(
											expression, (MemberExpression)levelExpression, level,
											(n,ctx,ex,l,mex) =>
											{
												switch (n)
												{
													case 0 :
														var buildExpression = GetExpression(expression, levelExpression, mex);
														return ConvertExpressions(buildExpression, flags);
													default:
														return ctx.ConvertToSql(ex, l, flags);
												}
											});
									}

								case ExpressionType.Parameter:
									if (levelExpression != expression)
										return GetSequence(expression, level).ConvertToSql(expression, level + 1, flags);

									if (level == 0)
										return GetSequence(expression, level).ConvertToSql(null, 0, flags);

									break;

								default:
									if (level == 0)
										return Builder.ConvertExpressions(this, expression, flags);
									break;
							}

							break;
						}
				}
			}

			throw new NotImplementedException();
		}

		SqlInfo[] ConvertMember(MemberInfo member, Expression expression, ConvertFlags flags)
		{
			return ConvertExpressions(expression, flags)
				.Select(si => si.Clone(member))
				.ToArray();
		}

		SqlInfo[] ConvertExpressions(Expression expression, ConvertFlags flags)
		{
			return Builder.ConvertExpressions(this, expression, flags)
				.Select (CheckExpression)
				.ToArray();
		}

		SqlInfo CheckExpression(SqlInfo expression)
		{
			if (expression.Sql is SqlSearchCondition)
			{
				expression.Sql = Builder.Convert(
					this,
					new SqlFunction(typeof(bool), "CASE", expression.Sql, new SqlValue(true), new SqlValue(false)));
			}

			return expression;
		}

		#endregion

		#region ConvertToIndex

		readonly Dictionary<Tuple<Expression,int,ConvertFlags>,SqlInfo[]> _expressionIndex = new Dictionary<Tuple<Expression,int,ConvertFlags>,SqlInfo[]>();

		public virtual SqlInfo[] ConvertToIndex(Expression expression, int level, ConvertFlags flags)
		{
			var key = Tuple.Create(expression, level, flags);

			if (!_expressionIndex.TryGetValue(key, out var info))
			{
				info = ConvertToIndexInternal(expression, level, flags);

				var newInfo = info
					.Select(i =>
					{
						if (i.Query == SelectQuery)
							return i;

						return new SqlInfo(i.MemberChain)
						{
							Query = SelectQuery,
							Index = SelectQuery.Select.Add(i.Query.Select.Columns[i.Index])
						};
					})
					.ToArray();

				_expressionIndex.Add(key, newInfo);

				return newInfo;
			}

			return info;
		}

		readonly Dictionary<Tuple<MemberInfo,ConvertFlags>,SqlInfo[]> _memberIndex = new Dictionary<Tuple<MemberInfo,ConvertFlags>,SqlInfo[]>();

		class SqlData
		{
			public SqlInfo[]  Sql;
			public MemberInfo Member;
		}

		SqlInfo[] ConvertToIndexInternal(Expression expression, int level, ConvertFlags flags)
		{
			if (IsScalar)
			{
				if (Body.NodeType == ExpressionType.Parameter)
					for (var i = 0; i < Sequence.Length; i++)
						if (Body == Lambda.Parameters[i])
							return Sequence[i].ConvertToIndex(expression, level, flags);

				if (expression == null)
				{
					var key = Tuple.Create((MemberInfo)null, flags);

					if (!_memberIndex.TryGetValue(key, out var idx))
					{
						idx = ConvertToSql(null, 0, flags);

						foreach (var info in idx)
							SetInfo(info, null);

						_memberIndex.Add(key, idx);
					}

					return idx;
				}

				switch (flags)
				{
					case ConvertFlags.Field :
					case ConvertFlags.Key   :
					case ConvertFlags.All   :
						return ProcessScalar(
							expression,
							level,
							(ctx, ex, l) => ctx.ConvertToIndex(ex, l, flags),
							() => GetSequence(expression, level).ConvertToIndex(expression, level + 1, flags));
				}
			}
			else
			{
				if (expression == null)
				{
					switch (flags)
					{
						case ConvertFlags.Field :
						case ConvertFlags.Key   :
						case ConvertFlags.All   :
							{
								var p = Expression.Parameter(Body.Type, "p");
								var q =
									from m in Members.Keys
									where !(m is MethodInfo)
									select new SqlData
									{
										Sql    = ConvertToIndex(Expression.MakeMemberAccess(p, m), 1, flags),
										Member = m
									} into mm
									from m in mm.Sql.Select(s => s.Clone(mm.Member))
									select m;

								return q.ToArray();
							}
					}
				}

				switch (flags)
				{
					case ConvertFlags.All   :
					case ConvertFlags.Key   :
					case ConvertFlags.Field :
						{
							if (level == 0)
							{
								var idx = Builder.ConvertExpressions(this, expression, flags);

								foreach (var info in idx)
									SetInfo(info, null);

								return idx;
							}

							var levelExpression = expression.GetLevelExpression(Builder.MappingSchema, level);

							switch (levelExpression.NodeType)
							{
								case ExpressionType.MemberAccess :
									{
										if (levelExpression == expression)
										{
											var member = Tuple.Create(((MemberExpression)levelExpression).Member, flags);

											if (!_memberIndex.TryGetValue(member, out var idx))
											{
												idx = ConvertToSql(expression, level, flags);

												if (flags == ConvertFlags.Field && idx.Length != 1)
													throw new InvalidOperationException();

												foreach (var info in idx)
													SetInfo(info, member.Item1);

												_memberIndex.Add(member, idx);
											}

											return idx;
										}

										return ProcessMemberAccess(
											expression,
											(MemberExpression)levelExpression,
											level,
											(n, ctx, ex, l, _) => n == 0 ?
												GetSequence(expression, level).ConvertToIndex(expression, level + 1, flags) :
												ctx.ConvertToIndex(ex, l, flags));
									}

								case ExpressionType.Parameter:

									if (levelExpression != expression)
										return GetSequence(expression, level).ConvertToIndex(expression, level + 1, flags);
									break;
							}

							break;
						}
				}
			}

			throw new NotImplementedException();
		}

		void SetInfo(SqlInfo info, MemberInfo member)
		{
			info.Query = SelectQuery;

			if (info.Sql == SelectQuery)
				info.Index = SelectQuery.Select.Columns.Count - 1;
			else
			{
				info.Index = SelectQuery.Select.Add(info.Sql);
				if (member != null)
					SelectQuery.Select.Columns[info.Index].Alias = member.Name;
			}
		}

		#endregion

		#region IsExpression

		Expression _lastAssociationExpression;
		int        _lastAssociationLevel = -1;

		public virtual IsExpressionResult IsExpression(Expression expression, int level, RequestFor requestFlag)
		{
			switch (requestFlag)
			{
				case RequestFor.Association :
					if (expression == _lastAssociationExpression && level == _lastAssociationLevel)
						return IsExpressionResult.False;

					_lastAssociationExpression = expression;
					_lastAssociationLevel      = level;

					break;
			}

			var res = IsExpressionInternal(expression, level, requestFlag);

			switch (requestFlag)
			{
				case RequestFor.Association :
					_lastAssociationExpression = null;
					_lastAssociationLevel      = -1;
					break;
			}

			return res;
		}

		public IsExpressionResult IsExpressionInternal(Expression expression, int level, RequestFor requestFlag)
		{
			switch (requestFlag)
			{
				case RequestFor.SubQuery : return IsExpressionResult.False;
				case RequestFor.Root     :
					return new IsExpressionResult(Sequence.Length == 1 ?
						ReferenceEquals(expression, Lambda.Parameters[0]) :
						Lambda.Parameters.Any(p => ReferenceEquals(p, expression)));
			}

			if (IsScalar)
			{
				if (expression == null)
					return IsExpression(Body, 0, requestFlag);

				switch (requestFlag)
				{
					case RequestFor.Table       :
					case RequestFor.Association :
					case RequestFor.Field       :
					case RequestFor.Expression  :
					case RequestFor.Object      :
					case RequestFor.GroupJoin   :
						return ProcessScalar(
							expression,
							level,
							(ctx, ex, l) => ctx.IsExpression(ex, l, requestFlag),
							() => new IsExpressionResult(requestFlag == RequestFor.Expression));
					default                     : return IsExpressionResult.False;
				}
			}
			else
			{
				switch (requestFlag)
				{
					case RequestFor.Table       :
					case RequestFor.Association :
					case RequestFor.Field       :
					case RequestFor.Expression  :
					case RequestFor.Object      :
					case RequestFor.GroupJoin   :
						{
							if (expression == null)
							{
								if (requestFlag == RequestFor.Expression)
									return new IsExpressionResult(Members.Values.Any(member => IsExpression(member, 0, requestFlag).Result));

								return new IsExpressionResult(requestFlag == RequestFor.Object);
							}

							var levelExpression = expression.GetLevelExpression(Builder.MappingSchema, level);

							switch (levelExpression.NodeType)
							{
								case ExpressionType.MemberAccess :
									{
										var member = ((MemberExpression)levelExpression).Member;

										if (!Members.TryGetValue(member, out var memberExpression))
										{
											var nm = Members.Keys.FirstOrDefault(m => m.Name == member.Name);

											if (nm != null && member.DeclaringType.IsInterfaceEx())
											{
												if (member.DeclaringType.IsSameOrParentOf(nm.DeclaringType))
													memberExpression = Members[nm];
												else
												{
													var mdt = member.DeclaringType.GetDefiningTypes(member);
													var ndt = Body.Type.           GetDefiningTypes(nm);

													if (mdt.Intersect(ndt).Any())
														memberExpression = Members[nm];
												}
											}

											if (memberExpression == null)
												return new IsExpressionResult(requestFlag == RequestFor.Expression);
												//throw new InvalidOperationException(
												//	string.Format("Invalid member '{0}.{1}'", member.DeclaringType, member.Name));
										}

										if (ReferenceEquals(levelExpression, expression))
										{
											switch (memberExpression.NodeType)
											{
												case ExpressionType.New        :
												case ExpressionType.MemberInit :
													return new IsExpressionResult(requestFlag == RequestFor.Object);
											}
										}

										return ProcessMemberAccess(
											expression,
											(MemberExpression)levelExpression,
											level,
											(n,ctx,ex,l,ex1) => n == 0 ?
												new IsExpressionResult(requestFlag == RequestFor.Expression, ex1) :
												ctx.IsExpression(ex, l, requestFlag));
									}

								case ExpressionType.Parameter    :
									{
										var sequence  = GetSequence(expression, level);
										var parameter = Lambda.Parameters[Sequence.Length == 0 ? 0 : Array.IndexOf(Sequence, sequence)];

										if (ReferenceEquals(levelExpression, expression))
										{
											if (ReferenceEquals(levelExpression, parameter))
												return sequence.IsExpression(null, 0, requestFlag);
										}
										else if (level == 0)
											return sequence.IsExpression(expression, 1, requestFlag);

										break;
									}

								case ExpressionType.New          :
								case ExpressionType.MemberInit   : return new IsExpressionResult(requestFlag == RequestFor.Object);
								default                          : return new IsExpressionResult(requestFlag == RequestFor.Expression);
							}

							break;
						}
					default: return IsExpressionResult.False;
				}
			}

			throw new NotImplementedException();
		}

		#endregion

		#region GetContext

		public virtual IBuildContext GetContext(Expression expression, int level, BuildInfo buildInfo)
		{
			if (expression == null)
				return this;

			if (IsScalar)
			{
				return ProcessScalar(
					expression,
					level,
					(ctx, ex, l) => ctx.GetContext(ex, l, buildInfo),
					() => throw new NotImplementedException());
			}
			else
			{
				var levelExpression = expression.GetLevelExpression(Builder.MappingSchema, level);

				switch (levelExpression.NodeType)
				{
					case ExpressionType.MemberAccess :
						{
							if (levelExpression == expression && Sequence.Length == 1 && !(Sequence[0] is GroupByBuilder.GroupByContext))
							{
								var memberExpression = GetMemberExpression(
									((MemberExpression)levelExpression).Member,
									levelExpression == expression,
									levelExpression.Type,
									expression);

								return GetContext(memberExpression, 0, new BuildInfo(this, memberExpression, buildInfo.SelectQuery));
							}

							var context = ProcessMemberAccess(
								expression,
								(MemberExpression)levelExpression,
								level,
								(n,ctx,ex,l,_) => n == 0 ?
									null :
									ctx.GetContext(ex, l, buildInfo));

							if (context == null)
								throw new NotImplementedException();

							return context;
						}

					case ExpressionType.Parameter    :
						{
							var sequence  = GetSequence(expression, level);
							var parameter = Lambda.Parameters[Sequence.Length == 0 ? 0 : Array.IndexOf(Sequence, sequence)];

							if (ReferenceEquals(levelExpression, expression))
							{
								if (ReferenceEquals(levelExpression, parameter))
									return sequence.GetContext(null, 0, buildInfo);
							}
							else if (level == 0)
								return sequence.GetContext(expression, 1, buildInfo);

							break;
						}
				}

				if (level == 0)
				{
					var sequence = GetSequence(expression, level);
					return sequence.GetContext(expression, level + 1, buildInfo);
				}
			}

			throw new NotImplementedException();
		}

		#endregion

		#region ConvertToParentIndex

		public virtual int ConvertToParentIndex(int index, IBuildContext context)
		{
			if (!ReferenceEquals(context.SelectQuery, SelectQuery))
				index = SelectQuery.Select.Add(context.SelectQuery.Select.Columns[index]);

			return Parent?.ConvertToParentIndex(index, this) ?? index;
		}

		#endregion

		#region SetAlias

		public virtual void SetAlias(string alias)
		{
			if (!alias.IsNullOrEmpty() && !alias.Contains('<') && SelectQuery.Select.From.Tables.Count == 1)
			{
				SelectQuery.Select.From.Tables[0].Alias = alias;
			}
		}

		#endregion

		#region GetSubQuery

		public ISqlExpression GetSubQuery(IBuildContext context)
		{
			return null;
		}

		#endregion

		public virtual SqlStatement GetResultStatement()
		{
			return Statement ?? (Statement = new SqlSelectStatement(SelectQuery));
		}

		#region Helpers

		T ProcessScalar<T>(Expression expression, int level, Func<IBuildContext,Expression,int,T> action, Func<T> defaultAction)
		{
			if (level == 0)
			{
				if (Body.NodeType == ExpressionType.Parameter && Lambda.Parameters.Count == 1)
				{
					var sequence = GetSequence(Body, 0);

					return ReferenceEquals(expression, Body) ?
						action(sequence, null,       0) :
						action(sequence, expression, 1);
				}

				var levelExpression = expression.GetLevelExpression(Builder.MappingSchema, level);

				if (!ReferenceEquals(levelExpression, expression))
				{
					var ctx = GetSequence(expression, level);
					return ctx == null ? defaultAction() : action(ctx, expression, Sequence.Contains(ctx) ? level + 1 : 0);
				}

				if (expression.NodeType == ExpressionType.Parameter)
				{
					var sequence  = GetSequence(expression, level);
					var parameter = Lambda.Parameters[Sequence.Length == 0 ? 0 : Array.IndexOf(Sequence, sequence)];

					if (ReferenceEquals(levelExpression, parameter))
						return action(sequence, null, 0);
				}

				switch (Body.NodeType)
				{
					case ExpressionType.MemberAccess : return action(GetSequence(expression, level), null, 0);
					default                          : return defaultAction();
				}
			}
			else
			{
				var root = Body.GetRootObject(Builder.MappingSchema);

				if (root.NodeType == ExpressionType.Parameter)
				{
					var levelExpression = expression.GetLevelExpression(Builder.MappingSchema, level - 1);
					var newExpression   = GetExpression(expression, levelExpression, Body);

					Builder.UpdateConvertedExpression(expression, newExpression);

					var result = action(this, newExpression, 0);

					Builder.RemoveConvertedExpression(newExpression);

					return result;
				}
			}

			throw new NotImplementedException();
		}

		T ProcessMemberAccess<T>(Expression expression, MemberExpression levelExpression, int level,
			Func<int,IBuildContext,Expression,int,Expression,T> action)
		{
			var memberExpression = Members[levelExpression.Member];
			var newExpression    = GetExpression(expression, levelExpression, memberExpression);
			var sequence         = GetSequence  (expression, level);
			var nextLevel        = 1;

			if (sequence != null)
			{
				var idx = Sequence.Length == 0 ? 0 : Array.IndexOf(Sequence, sequence);

				if (idx >= 0)
				{
					var parameter = Lambda.Parameters[idx];

					if (ReferenceEquals(memberExpression, parameter) && ReferenceEquals(levelExpression, expression))
						return action(1, sequence, null, 0, memberExpression);
				}
				else
				{
					nextLevel = 0;
				}
			}

			switch (memberExpression.NodeType)
			{
				case ExpressionType.MemberAccess :
				case ExpressionType.Parameter    :
					if (sequence != null)
						return action(2, sequence, newExpression, nextLevel, memberExpression);
					break;

				case ExpressionType.New          :
				case ExpressionType.MemberInit   :
					{
						var mmExpresion = GetMemberExpression(memberExpression, expression, level + 1);
						return action(3, this, mmExpresion, 0, memberExpression);
					}
			}

			return action(0, this, null, 0, memberExpression);
		}

		protected bool IsSubQuery()
		{
			for (var p = Parent; p != null; p = p.Parent)
				if (p.IsExpression(null, 0, RequestFor.SubQuery).Result)
					return true;
			return false;
		}

		IBuildContext GetSequence(Expression expression, int level)
		{
			if (Sequence.Length == 1 && Sequence[0].Parent == null)
				return Sequence[0];

			Expression root = null;

			if (IsScalar)
			{
				root = expression.GetRootObject(Builder.MappingSchema);
			}
			else
			{
				var levelExpression = expression.GetLevelExpression(Builder.MappingSchema, level);

				switch (levelExpression.NodeType)
				{
					case ExpressionType.MemberAccess :
						{
							var memberExpression = Members[((MemberExpression)levelExpression).Member];

							root =  memberExpression.GetRootObject(Builder.MappingSchema);

							if (root.NodeType != ExpressionType.Parameter)
								return null;

							break;
						}

					case ExpressionType.Parameter :
						{
							root = expression.GetRootObject(Builder.MappingSchema);
							break;
						}
				}
			}

			if (root != null)
				for (var i = 0; i < Lambda.Parameters.Count; i++)
					if (ReferenceEquals(root, Lambda.Parameters[i]))
						return Sequence[i];

			foreach (var context in Sequence)
			{
				if (context.Parent != null)
				{
					var ctx = Builder.GetContext(context, root);
					if (ctx != null)
						return ctx;
				}
			}

			return null;
		}

		static Expression GetExpression(Expression expression, Expression levelExpression, Expression memberExpression)
		{
			return !ReferenceEquals(levelExpression, expression) ?
				expression.Transform(ex => ReferenceEquals(ex, levelExpression) ? memberExpression : ex) :
				memberExpression;
		}

		Expression GetMemberExpression(Expression newExpression, Expression expression, int level)
		{
			var levelExpresion = expression.GetLevelExpression(Builder.MappingSchema, level);

			switch (newExpression.NodeType)
			{
				case ExpressionType.New        :
				case ExpressionType.MemberInit : break;
				default                        :
					var le = expression.GetLevelExpression(Builder.MappingSchema, level - 1);
					return GetExpression(expression, le, newExpression);
			}

			if (levelExpresion.NodeType != ExpressionType.MemberAccess)
				throw new LinqException("Invalid expression {0}", levelExpresion);

			var me = (MemberExpression)levelExpresion;

			switch (newExpression.NodeType)
			{
				case ExpressionType.New:
					{
						var expr = (NewExpression)newExpression;

// ReSharper disable ConditionIsAlwaysTrueOrFalse
// ReSharper disable HeuristicUnreachableCode
						if (expr.Members == null)
							throw new LinqException("Invalid expression {0}", expression);
// ReSharper restore HeuristicUnreachableCode
// ReSharper restore ConditionIsAlwaysTrueOrFalse

						for (var i = 0; i < expr.Members.Count; i++)
							if (me.Member == expr.Members[i])
								return ReferenceEquals(levelExpresion, expression) ?
									expr.Arguments[i].Unwrap() :
									GetMemberExpression(expr.Arguments[i].Unwrap(), expression, level + 1);

						throw new LinqException("Invalid expression {0}", expression);
					}

				case ExpressionType.MemberInit:
					{
						var expr = (MemberInitExpression)newExpression;

						foreach (var binding in expr.Bindings.Cast<MemberAssignment>())
						{
							if (me.Member.EqualsTo(binding.Member))
								return ReferenceEquals(levelExpresion, expression) ?
									binding.Expression.Unwrap() :
									GetMemberExpression(binding.Expression.Unwrap(), expression, level + 1);
						}

						throw new LinqException("Invalid expression {0}", expression);
					}
			}

			return expression;
		}

		protected Expression GetMemberExpression(MemberInfo member, bool add, Type type, Expression sourceExpression)
		{
			if (!Members.TryGetValue(member, out var memberExpression))
			{
				foreach (var m in Members)
				{
					if (m.Key.Name == member.Name)
					{
						if (m.Key.EqualsTo(member, IsScalar ? null : Body.Type))
							return m.Value;
					}
				}

				if (member.DeclaringType.IsSameOrParentOf(Body.Type))
				{
					if (Body.NodeType == ExpressionType.MemberInit)
					{
						var ed = Builder.MappingSchema.GetEntityDescriptor(Body.Type);

						if (ed.Aliases != null)
						{
							if (ed.Aliases.TryGetValue(member.Name, out var value))
								return GetMemberExpression(ed.TypeAccessor[value].MemberInfo, add, type, sourceExpression);

							foreach (var a in ed.Aliases)
							{
								if (a.Value == member.Name)
								{
									foreach (var m in Members)
										if (m.Key.Name == a.Key)
											return m.Value;

									break;
								}
							}
						}
					}

					if (add)
					{
						memberExpression = Expression.Constant(type.GetDefaultValue(), type);
						Members.Add(member, memberExpression);

						return memberExpression;
					}
				}

				throw new LinqToDBException($"'{sourceExpression}' cannot be converted to SQL.");
			}

			return memberExpression;
		}

		#endregion
	}
}
