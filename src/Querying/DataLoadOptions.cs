using System;
using System.Collections.Generic;
using System.Text;
using System.Reflection;
using System.Linq;
using System.Linq.Expressions;
using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Data.Linq.Provider.Common;
using System.Data.Linq.Provider.Visitors;

// Namespace has to stay System.Data.Linq for backwards compatibility, as this is a public class used in user code.
namespace System.Data.Linq
{
	sealed public class DataLoadOptions
	{
		#region Member Declarations
		bool frozen;
		private Dictionary<MetaPosition, MemberInfo> includes = new Dictionary<MetaPosition, MemberInfo>();
		private Dictionary<MetaPosition, LambdaExpression> subqueries = new Dictionary<MetaPosition, LambdaExpression>();
		#endregion

		#region Private classes
		private static class Searcher
		{
			static internal MemberInfo MemberInfoOf(LambdaExpression lambda)
			{
				Visitor v = new Visitor();
				v.VisitLambda(lambda);
				return v.MemberInfo;
			}

			private class Visitor : System.Data.Linq.Provider.Visitors.ExpressionVisitor
			{
				internal MemberInfo MemberInfo;
				internal override Expression VisitMemberAccess(MemberExpression m)
				{
					this.MemberInfo = m.Member;
					return base.VisitMemberAccess(m);
				}

				internal override Expression VisitMethodCall(MethodCallExpression m)
				{
					this.Visit(m.Object);
					foreach(Expression arg in m.Arguments)
					{
						this.Visit(arg);
						break; // Only follow the extension method 'this'
					}
					return m;
				}

			}
		}
		#endregion


		/// <summary>
		/// Describe a property that is automatically loaded when the containing instance is loaded
		/// </summary>
		[SuppressMessage("Microsoft.Design", "CA1006:DoNotNestGenericTypesInMemberSignatures", Justification = "[....]: Generic types are an important part of Linq APIs and they could not exist without nested generic support.")]
		[SuppressMessage("Microsoft.Design", "CA1011:ConsiderPassingBaseTypesAsParameters", Justification = "[....]: Need to provide static typing.")]
		public void LoadWith<T>(Expression<Func<T, object>> expression)
		{
			if(expression == null)
			{
				throw Error.ArgumentNull("expression");
			}
			MemberInfo mi = GetLoadWithMemberInfo(expression);
			this.Preload(mi);
		}

		/// <summary>
		/// Describe a property that is automatically loaded when the containing instance is loaded
		/// </summary>
		public void LoadWith(LambdaExpression expression)
		{
			if(expression == null)
			{
				throw Error.ArgumentNull("expression");
			}
			MemberInfo mi = GetLoadWithMemberInfo(expression);
			this.Preload(mi);
		}

		/// <summary>
		/// Place a subquery on the given association.
		/// </summary>
		[SuppressMessage("Microsoft.Design", "CA1006:DoNotNestGenericTypesInMemberSignatures", Justification = "[....]: Generic types are an important part of Linq APIs and they could not exist without nested generic support.")]
		[SuppressMessage("Microsoft.Design", "CA1011:ConsiderPassingBaseTypesAsParameters", Justification = "[....]: Need to provide static typing.")]
		public void AssociateWith<T>(Expression<Func<T, object>> expression)
		{
			if(expression == null)
			{
				throw Error.ArgumentNull("expression");
			}
			this.AssociateWithInternal(expression);
		}

		/// <summary>
		/// Place a subquery on the given association.
		/// </summary>
		public void AssociateWith(LambdaExpression expression)
		{
			if(expression == null)
			{
				throw Error.ArgumentNull("expression");
			}
			this.AssociateWithInternal(expression);
		}

		private void AssociateWithInternal(LambdaExpression expression)
		{
			// Strip the cast-to-object.
			Expression op = expression.Body;
			while(op.NodeType == ExpressionType.Convert || op.NodeType == ExpressionType.ConvertChecked)
			{
				op = ((UnaryExpression)op).Operand;
			}
			LambdaExpression lambda = Expression.Lambda(op, expression.Parameters.ToArray());
			MemberInfo mi = Searcher.MemberInfoOf(lambda);
			this.Subquery(mi, lambda);
		}

		/// <summary>
		/// Determines if the member is automatically loaded with its containing instances.
		/// </summary>
		/// <param name="member">The member this is automatically loaded.</param>
		/// <returns>True if the member is automatically loaded.</returns>
		internal bool IsPreloaded(MemberInfo member)
		{
			if(member == null)
			{
				throw Error.ArgumentNull("member");
			}
			return includes.ContainsKey(new MetaPosition(member));
		}

		/// <summary>        
		/// Two shapes are equivalent if any of the following are true:
		///  (1) They are the same object instance
		///  (2) They are both null or empty
		///  (3) They contain the same preloaded members
		/// </summary>
		internal static bool ShapesAreEquivalent(DataLoadOptions ds1, DataLoadOptions ds2)
		{
			bool shapesAreSameOrEmpty = (ds1 == ds2) || ((ds1 == null || ds1.IsEmpty) && (ds2 == null || ds2.IsEmpty));
			if(!shapesAreSameOrEmpty)
			{
				if(ds1 == null || ds2 == null || ds1.includes.Count != ds2.includes.Count)
				{
					return false;
				}

				foreach(MetaPosition metaPosition in ds2.includes.Keys)
				{
					if(!ds1.includes.ContainsKey(metaPosition))
					{
						return false;
					}
				}
			}
			return true;
		}

		/// <summary>
		/// Gets the subquery expression associated with the member.
		/// </summary>
		/// <param name="member">The member with the subquery.</param>
		/// <returns></returns>
		internal LambdaExpression GetAssociationSubquery(MemberInfo member)
		{
			if(member == null)
			{
				throw Error.ArgumentNull("member");
			}
			LambdaExpression expression = null;
			subqueries.TryGetValue(new MetaPosition(member), out expression);
			return expression;
		}

		/// <summary>
		/// Freeze the shape. Any further attempts to modify the shape will result in 
		/// an exception.
		/// </summary>
		internal void Freeze()
		{
			this.frozen = true;
		}

		/// <summary>
		/// Describe a property that is automatically loaded when the containing instance is loaded
		/// </summary>
		internal void Preload(MemberInfo association)
		{
			if(association == null)
			{
				throw Error.ArgumentNull("association");
			}
			if(this.frozen)
			{
				throw Error.IncludeNotAllowedAfterFreeze();
			}
			this.includes.Add(new MetaPosition(association), association);
			ValidateTypeGraphAcyclic();
		}

		/// <summary>
		/// Place a subquery on the given association.
		/// </summary>
		private void Subquery(MemberInfo association, LambdaExpression subquery)
		{
			if(this.frozen)
			{
				throw Error.SubqueryNotAllowedAfterFreeze();
			}
			subquery = (LambdaExpression)Funcletizer.Funcletize(subquery); // Layering violation.
			ValidateSubqueryMember(association);
			ValidateSubqueryExpression(subquery);
			this.subqueries[new MetaPosition(association)] = subquery;
		}

		/// <summary>
		/// If the lambda specified is of the form p.A, where p is the parameter
		/// and A is a member on p, the MemberInfo for A is returned.  If
		/// the expression is not of this form, an exception is thrown.
		/// </summary>
		private static MemberInfo GetLoadWithMemberInfo(LambdaExpression lambda)
		{
			// When the specified member is a value type, there will be a conversion
			// to object that we need to strip
			Expression body = lambda.Body;
			if(body.NodeType == ExpressionType.Convert || body.NodeType == ExpressionType.ConvertChecked)
			{
				body = ((UnaryExpression)body).Operand;
			}

			MemberExpression mex = body as MemberExpression;
			if(mex != null && mex.Expression.NodeType == ExpressionType.Parameter)
			{
				return mex.Member;
			}
			else
			{
				throw Error.InvalidLoadOptionsLoadMemberSpecification();
			}
		}


		private void ValidateTypeGraphAcyclic()
		{
			IEnumerable<MemberInfo> edges = this.includes.Values;
			int removed = 0;

			for(int loop = 0; loop < this.includes.Count; ++loop)
			{
				// Build a list of all edge targets.
				HashSet<Type> edgeTargets = new HashSet<Type>();
				foreach(MemberInfo edge in edges)
				{
					edgeTargets.Add(GetIncludeTarget(edge));
				}
				// Remove all edges with sources matching no target.
				List<MemberInfo> newEdges = new List<MemberInfo>();
				bool someRemoved = false;
				foreach(MemberInfo edge in edges)
				{
					if(edgeTargets.Where(et => et.IsAssignableFrom(edge.DeclaringType) || edge.DeclaringType.IsAssignableFrom(et)).Any())
					{
						newEdges.Add(edge);
					}
					else
					{
						++removed;
						someRemoved = true;
						if(removed == this.includes.Count)
							return;
					}
				}
				if(!someRemoved)
				{
					throw Error.IncludeCycleNotAllowed(); // No edges removed, there must be a loop.
				}
				edges = newEdges;
			}
			throw new InvalidOperationException("Bug in ValidateTypeGraphAcyclic"); // Getting here means a bug.
		}

		private static Type GetIncludeTarget(MemberInfo mi)
		{
			Type mt = TypeSystem.GetMemberType(mi);
			if(mt.IsGenericType)
			{
				return mt.GetGenericArguments()[0];
			}
			return mt;
		}

		private static void ValidateSubqueryMember(MemberInfo mi)
		{
			Type memberType = TypeSystem.GetMemberType(mi);
			if(memberType == null)
			{
				throw Error.SubqueryNotSupportedOn(mi);
			}
			if(!typeof(IEnumerable).IsAssignableFrom(memberType))
			{
				throw Error.SubqueryNotSupportedOnType(mi.Name, mi.DeclaringType);
			}
		}

		private static void ValidateSubqueryExpression(LambdaExpression subquery)
		{
			if(!typeof(IEnumerable).IsAssignableFrom(subquery.Body.Type))
			{
				throw Error.SubqueryMustBeSequence();
			}
			new SubqueryValidator().VisitLambda(subquery);
		}

		/// <summary>
		/// Whether there have been LoadOptions specified.
		/// </summary>
		internal bool IsEmpty
		{
			get { return this.includes.Count == 0 && this.subqueries.Count == 0; }
		}
	}
}
