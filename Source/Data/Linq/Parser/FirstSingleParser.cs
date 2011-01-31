﻿using System;
using System.Linq;
using System.Linq.Expressions;

namespace BLToolkit.Data.Linq.Parser
{
	using Data.Sql;

	class FirstSingleParser : MethodCallParser
	{
		protected override bool CanParseMethodCall(ExpressionParser parser, MethodCallExpression methodCall, SqlQuery sqlQuery)
		{
			if (!methodCall.IsQueryable())
				return false;

			switch (methodCall.Method.Name)
			{
				case "First"           :
				case "FirstOrDefault"  :
				case "Single"          :
				case "SingleOrDefault" : return true;
				default                : return false;
			}
		}

		protected override IParseContext ParseMethodCall(ExpressionParser parser, MethodCallExpression methodCall, SqlQuery sqlQuery)
		{
			var sequence = parser.ParseSequence(methodCall.Arguments[0], sqlQuery);

			if (methodCall.Arguments.Count == 2)
			{
				var condition = (LambdaExpression)methodCall.Arguments[1].Unwrap();

				sequence = parser.ParseWhere(sequence, condition);
				sequence.SetAlias(condition.Parameters[0].Name);
			}

			var take = 0;

			if (!parser.IsSubQueryParsing || parser.SqlProvider.IsSubQueryTakeSupported)
				switch (methodCall.Method.Name)
				{
					case "First"           :
					case "FirstOrDefault"  :
						take = 1;
						break;

					case "Single"          :
					case "SingleOrDefault" :
						if (!parser.IsSubQueryParsing)
							take = 2;
							break;
				}

			if (take != 0)
				parser.ParseTake(sequence, new SqlValue(take));

			//sequence.BuildExpression(null, 0);

			return new FirstSingleContext(sequence, methodCall.Method.Name);
		}

		class FirstSingleContext : SequenceContextBase
		{
			public FirstSingleContext(IParseContext sequence, string methodName)
				: base(sequence, null)
			{
				_methodName = methodName;
			}

			readonly string _methodName;

			public override void BuildQuery<T>(Query<T> query, ParameterExpression queryParameter)
			{
				Sequence.BuildQuery(query, queryParameter);

				switch (_methodName)
				{
					case "First"           : query.GetElement = (ctx, db, expr, ps) => query.GetIEnumerable(ctx, db, expr, ps).First();           break;
					case "FirstOrDefault"  : query.GetElement = (ctx, db, expr, ps) => query.GetIEnumerable(ctx, db, expr, ps).FirstOrDefault();  break;
					case "Single"          : query.GetElement = (ctx, db, expr, ps) => query.GetIEnumerable(ctx, db, expr, ps).Single();          break;
					case "SingleOrDefault" : query.GetElement = (ctx, db, expr, ps) => query.GetIEnumerable(ctx, db, expr, ps).SingleOrDefault(); break;
				}
			}

			public override Expression BuildExpression(Expression expression, int level)
			{
				throw new NotImplementedException();
				//return Sequence.BuildExpression(expression, level + 1);
			}

			public override ISqlExpression[] ConvertToSql(Expression expression, int level, ConvertFlags flags)
			{
				return Sequence.ConvertToSql(expression, level + 1, flags);
			}

			public override int[] ConvertToIndex(Expression expression, int level, ConvertFlags flags)
			{
				throw new NotImplementedException();
			}

			public override bool IsExpression(Expression expression, int level, RequestFor requestFlag)
			{
				switch (requestFlag)
				{
					case RequestFor.SubQuery : return false;
				}

				throw new NotImplementedException();
			}

			public override IParseContext GetContext(Expression expression, int level, SqlQuery currentSql)
			{
				throw new NotImplementedException();
			}
		}
	}
}