﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Linq.Expressions;

namespace BLToolkit.Data.Linq.Parser
{
	using Data.Sql;

	class GroupByParser : MethodCallParser
	{
		protected override bool CanParseMethodCall(ExpressionParser parser, MethodCallExpression methodCall, SqlQuery sqlQuery)
		{
			if (!methodCall.IsQueryable("GroupBy"))
				return false;

			var body = ((LambdaExpression)methodCall.Arguments[1].Unwrap()).Body.Unwrap();

			if (body.NodeType == ExpressionType	.MemberInit)
			{
				var mi = (MemberInitExpression)body;
				bool throwExpr;

				if (mi.NewExpression.Arguments.Count > 0 || mi.Bindings.Count == 0)
					throwExpr = true;
				else
					throwExpr = mi.Bindings.Any(b => b.BindingType != MemberBindingType.Assignment);

				if (throwExpr)
					throw new NotSupportedException(string.Format("Explicit construction of entity type '{0}' in group by is not allowed.", body.Type));
			}

			return (methodCall.Arguments[methodCall.Arguments.Count - 1].Unwrap().NodeType == ExpressionType.Lambda);
		}

		protected override IParseContext ParseMethodCall(ExpressionParser parser, MethodCallExpression methodCall, SqlQuery sqlQuery)
		{
			var sequence = parser.ParseSequence(methodCall.Arguments[0], sqlQuery);

			var keySelector = (LambdaExpression)methodCall.Arguments[1].Unwrap();

			LambdaExpression elementSelector = null;
			LambdaExpression resultSelector  = null;

			if (methodCall.Arguments.Count > 2)
			{
				elementSelector = (LambdaExpression)methodCall.Arguments[2].Unwrap();

				if (elementSelector.Parameters.Count == 2)
				{
					resultSelector  = elementSelector;
					elementSelector = null;
				}
				else if (methodCall.Arguments.Count > 3)
				{
					resultSelector = (LambdaExpression)methodCall.Arguments[3].Unwrap();
				}
			}

			var key = new KeyContext(keySelector, sequence);

			var groupSql = key.ConvertToSql(null, 0, ConvertFlags.All);

			if (groupSql.Any(s => !(s is SqlField || s is SqlQuery.Column)))
			{
				sequence = new SubQueryContext(sequence);
				key    = new KeyContext(keySelector, sequence);
				groupSql = key.ConvertToSql(null, 0, ConvertFlags.All);
			}
			else
			{
				// Can be used instead of GroupBy.Items.Clear().
				//
				//if (!wrap)
				//	wrap = CurrentSql.GroupBy.Items.Count > 0;

				sequence.SqlQuery.GroupBy.Items.Clear();

				new QueryVisitor().Visit(sequence.SqlQuery.From, e =>
				{
					if (e.ElementType == QueryElementType.JoinedTable)
					{
						var jt = (SqlQuery.JoinedTable)e;
						if (jt.JoinType == SqlQuery.JoinType.Inner)
							jt.IsWeak = false;
					}
				});
			}

			foreach (var sql in groupSql)
				sequence.SqlQuery.GroupBy.Expr(sql);

			var element = elementSelector != null ? new SelectContext(elementSelector, sequence) : null;
			var groupBy = new GroupByContext(methodCall, sequence, key, element);

			if (resultSelector != null)
				throw new NotImplementedException();
			//IParseContext result  = resultSelector  != null ? new SelectContext(resultSelector,  sequence) : null;)

			return groupBy;
		}

		class KeyContext : SelectContext
		{
			public KeyContext(LambdaExpression lambda, params IParseContext[] sequences)
				: base(lambda, sequences)
			{
			}
		}

		class GroupByContext : SequenceContextBase
		{
			public GroupByContext(MethodCallExpression methodCall, IParseContext sequence, KeyContext key, SelectContext element)
				: base(sequence, null)
			{
				_methodCall = methodCall;
				_sequence   = sequence;
				_key        = key;
				_element    = element;

				_groupingType = methodCall.Type.GetGenericArguments()[0];
			}

			readonly MethodCallExpression _methodCall;
			readonly IParseContext        _sequence;
			readonly KeyContext           _key;
			readonly SelectContext        _element;
			readonly Type                 _groupingType;

			class Grouping<TKey,TElement> : IGrouping<TKey,TElement>
			{

				public Grouping(TKey key, QueryContext queryContext, Func<IDataContext,TKey,IQueryable<TElement>> itemReader)
				{
					_queryContext = queryContext;
					Key = key;

					if (Common.Configuration.Linq.PreloadGroups)
					{
						_items = GetItems(queryContext, itemReader);
					}
					else
					{
						_queryContext = queryContext;
						_itemReader   = itemReader;
					}
				}

				private  IList<TElement>                              _items;
				readonly QueryContext                                 _queryContext;
				readonly Func<IDataContext,TKey,IQueryable<TElement>> _itemReader;

				public TKey Key { get; private set; }

				List<TElement> GetItems(QueryContext queryContext, Func<IDataContext,TKey,IQueryable<TElement>> elementReader)
				{
					var db = queryContext.GetDataContext();

					try
					{
						return elementReader(db.DataContextInfo.DataContext, Key).ToList();
					}
					finally
					{
						queryContext.ReleaseDataContext(db);
					}
				}

				public IEnumerator<TElement> GetEnumerator()
				{
					if (_items == null)
						_items = GetItems(_queryContext, _itemReader);

					return _items.GetEnumerator();
				}

				IEnumerator IEnumerable.GetEnumerator()
				{
					return GetEnumerator();
				}
			}

			interface IGroupByHelper
			{
				Expression GetGrouping(GroupByContext context);
			}

			class GroupByHelper<TKey,TElement,TSource> : IGroupByHelper
			{
				public Expression GetGrouping(GroupByContext context)
				{
					var keyParam = Expression.Parameter(typeof(TKey), "key");

					var expr = Expression.Call(
						null,
						ReflectionHelper.Expressor<object>.MethodExpressor(_ => Queryable.Where(null, (Expression<Func<TSource,bool>>)null)),
						context._methodCall.Arguments[0],
						Expression.Lambda<Func<TSource,bool>>(
							Expression.Equal(context._key.Lambda.Body, keyParam),
							new[] { context._key.Lambda.Parameters[0] }));

					if (context._element != null)
					{
						expr = Expression.Call(
							null,
							ReflectionHelper.Expressor<object>.MethodExpressor(_ => Queryable.Select(null, (Expression<Func<TSource,TElement>>)null)),
							expr,
							context._element.Lambda);
					}

					var lambda = Expression.Lambda<Func<IDataContext,TKey,IQueryable<TElement>>>(
						Expression.Convert(expr, typeof(IQueryable<TElement>)),
						Expression.Parameter(typeof(IDataContext), "ctx"),
						keyParam);

					var itemReader = CompiledQuery.Compile(lambda);
					var keyExpr    = context._key.BuildExpression(null, 0);
					var keyReader  = Expression.Lambda<Func<QueryContext,IDataContext,IDataReader,Expression,object[],TKey>>(
						keyExpr,
						new []
						{
							ExpressionParser.ContextParam,
							ExpressionParser.DataContextParam,
							ExpressionParser.DataReaderParam,
							ExpressionParser.ExpressionParam,
							ExpressionParser.ParametersParam,
						});

					return Expression.Call(
						null,
						ReflectionHelper.Expressor<object>.MethodExpressor(_ => GetGrouping(null, null, null, null, null, null, null)),
						new Expression[]
						{
							ExpressionParser.ContextParam,
							ExpressionParser.DataContextParam,
							ExpressionParser.DataReaderParam,
							ExpressionParser.ExpressionParam,
							ExpressionParser.ParametersParam,
							Expression.Constant(keyReader.Compile()),
							Expression.Constant(itemReader),
						});
				}

				static IGrouping<TKey,TElement> GetGrouping(
					QueryContext             context,
					IDataContext             dataContext,
					IDataReader              dataReader,
					Expression               expr,
					object[]                 ps,
					Func<QueryContext,IDataContext,IDataReader,Expression,object[],TKey> keyReader,
					Func<IDataContext,TKey,IQueryable<TElement>>                         itemReader)
				{
					var key = keyReader(context, dataContext, dataReader, expr, ps);
					return new Grouping<TKey,TElement>(key, context, itemReader);
				}
			}

			Expression BuildGrouping()
			{
				var gtype  = typeof(GroupByHelper<,,>).MakeGenericType(
					_key.Lambda.Body.Type,
					_element != null ? _element.Lambda.Body.Type : _key.Lambda.Parameters[0].Type,
					_key.Lambda.Parameters[0].Type);

				var helper = (IGroupByHelper)Activator.CreateInstance(gtype);

				return helper.GetGrouping(this);
			}

			public override Expression BuildExpression(Expression expression, int level)
			{
				if (expression == null)
					return BuildGrouping();

				if (level != 0)
				{
					var levelExpression = expression.GetLevelExpression(level);

					if (levelExpression.NodeType == ExpressionType.MemberAccess)
					{
						var ma = (MemberExpression)levelExpression;

						if (ma.Member.Name == "Key" && ma.Member.DeclaringType == _groupingType)
						{
							return levelExpression == expression ?
								_key.BuildExpression(null,       0) :
								_key.BuildExpression(expression, level + 1);
						}
					}
				}

				throw new NotImplementedException();
			}

			public override ISqlExpression[] ConvertToSql(Expression expression, int level, ConvertFlags flags)
			{
				throw new NotImplementedException();
			}

			public override int[] ConvertToIndex(Expression expression, int level, ConvertFlags flags)
			{
				throw new NotImplementedException();
			}

			public override bool IsExpression(Expression expression, int level, RequestFor requestFlag)
			{
				throw new NotImplementedException();
			}

			public override IParseContext GetContext(Expression expression, int level, SqlQuery currentSql)
			{
				throw new NotImplementedException();
			}
		}
	}
}