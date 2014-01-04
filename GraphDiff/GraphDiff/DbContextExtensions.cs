﻿/*
 * This code is provided as is with no warranty. If you find a bug please report it on github.
 * If you would like to use the code please leave this comment at the top of the page
 * License MIT (c) Brent McKendrick 2012
 */

using RefactorThis.GraphDiff.Internal;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Data.Entity;
using System.Data.Entity.Core.Metadata.Edm;
using System.Data.Entity.Core.Objects;
using System.Data.Entity.Infrastructure;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace RefactorThis.GraphDiff
{
	public static class DbContextExtensions
	{
	    /// <summary>
	    /// Attaches a graph of entities and performs an update to the data store.
	    /// Author: (c) Brent McKendrick 2012
	    /// </summary>
	    /// <typeparam name="T">The type of the root entity</typeparam>
        /// <param name="context">The database context to attach / detach.</param>
	    /// <param name="entity">The root entity.</param>
	    /// <param name="mapping">The mapping configuration to define the bounds of the graph</param>
	    public static void UpdateGraph<T>(this DbContext context, T entity, Expression<Func<IUpdateConfiguration<T>, object>> mapping) where T : class, new()
		{
			// Guard null mapping
			if (mapping == null)
			{
				// Redirect to simple update
				UpdateGraph(context, entity);
				return;
			}

			bool isAutoDetectEnabled = context.Configuration.AutoDetectChangesEnabled;
			try
			{
				// performance improvement for large graphs
				context.Configuration.AutoDetectChangesEnabled = false;

				// Parse mapping tree
				var tree = new UpdateConfigurationVisitor<T>().GetUpdateMembers(mapping);
				var includeStrings = EntityFrameworkIncludeHelper.GetIncludeStrings(tree);

				// Get our entity with all includes needed, or add
                T existing = AddOrUpdateEntity(context, entity, includeStrings.ToArray());

				// Foreach branch perform recursive update
				foreach (var member in tree.Members)
					RecursiveGraphUpdate(context, existing, entity, member);
			}
			finally
			{
				context.Configuration.AutoDetectChangesEnabled = isAutoDetectEnabled;
			}
		}

		/// <summary>
		/// Attaches a graph of entities and performs an update to the data store.
		/// </summary>
        /// <param name="context">The database context to attach / detach.</param>
		/// <typeparam name="T">The type of the root entity</typeparam>
		/// <param name="entity">The root entity.</param>
        public static void UpdateGraph<T>(this DbContext context, T entity) where T : class, new()
		{
            AddOrUpdateEntity(context, entity);
		}

		#region Private

        private static T AddOrUpdateEntity<T>(this DbContext context, T entity, params string[] includes) where T : class, new()
        {
            if (entity == null)
                throw new ArgumentNullException("entity");

            T existing = context.FindEntityMatching(entity, includes);
            if (existing == null)
            {
                existing = new T();
                context.Set<T>().Add(existing);
            }
            context.UpdateValuesWithConcurrencyCheck(entity, existing);

            return existing;
        }

	    private static void RecursiveGraphUpdate(DbContext context, object dataStoreEntity, object updatingEntity, UpdateMember member)
		{
			if (member.IsCollection)
				UpdateCollectionRecursive(context, dataStoreEntity, updatingEntity, member);
			else
				UpdateEntityRecursive(context, dataStoreEntity, updatingEntity, member);
		}

        private static void UpdateCollectionRecursive(DbContext context, object dataStoreEntity, object updatingEntity, UpdateMember member)
        {
            var updateValues = (IEnumerable)member.Accessor.GetValue(updatingEntity, null);
            var dbCollection = (IEnumerable)member.Accessor.GetValue(dataStoreEntity, null);

            if (updateValues == null)
                updateValues = new List<object>();

            Type dbCollectionType = member.Accessor.PropertyType;
            Type innerElementType;

            if (dbCollectionType.IsArray)
                innerElementType = dbCollectionType.GetElementType();
            else if (dbCollectionType.IsGenericType)
                innerElementType = dbCollectionType.GetGenericArguments()[0];
            else
                throw new InvalidOperationException("GraphDiff requires the collection to be either IEnumerable<T> or T[]");

            if (dbCollection == null)
            {
                var newDbCollectionType = !dbCollectionType.IsInterface ? dbCollectionType : typeof(List<>).MakeGenericType(innerElementType);
                dbCollection = (IEnumerable)Activator.CreateInstance(newDbCollectionType);
                member.Accessor.SetValue(dataStoreEntity, dbCollection, null);
            }

            var keyFields = context.GetKeysFor(ObjectContext.GetObjectType(innerElementType));
            var dbHash = dbCollection.Cast<object>().ToDictionary(item => CreateHash(keyFields, item));

            // Iterate through the elements from the updated graph and try to match them against the db graph.
            var additions = new List<object>();
            foreach (var updateItem in updateValues)
            {
                var key = CreateHash(keyFields, updateItem);

                // try to find in db collection
                object dbItem;
                if (dbHash.TryGetValue(key, out dbItem))
                {
                    // If we own the collection
                    if (member.IsOwned)
                    {
                        context.UpdateValuesWithConcurrencyCheck(updateItem, dbItem);

                        AttachCyclicNavigationProperty(context, dataStoreEntity, updateItem);

                        foreach (var childMember in member.Members)
                            RecursiveGraphUpdate(context, dbHash[key], updateItem, childMember);
                    }

                    dbHash.Remove(key); // remove to leave only db removals in the collection
                }
                else
                    additions.Add(updateItem);
            }

            // Removal of dbItem's left in the collection
            foreach (var dbItem in dbHash.Values)
            {
                // Own the collection so remove it completely.
                if (member.IsOwned)
                    context.Set(ObjectContext.GetObjectType(dbItem.GetType())).Remove(dbItem);

                dbCollection.GetType().GetMethod("Remove").Invoke(dbCollection, new[] { dbItem });
            }

            // Add elements marked for addition
            foreach (object newItem in additions)
            {
                if (!member.IsOwned)
                {
                    if (context.Entry(newItem).State == EntityState.Detached)
                        context.Set(ObjectContext.GetObjectType(newItem.GetType())).Attach(newItem);

                    context.ReloadEntity(newItem);
                }

                // Otherwise we will add to object
                dbCollection.GetType().GetMethod("Add").Invoke(dbCollection, new[] { newItem });
                AttachCyclicNavigationProperty(context, dataStoreEntity, newItem);
            }
        }

	    private static void UpdateEntityRecursive(DbContext context, object dataStoreEntity, object updatingEntity, UpdateMember member)
	    {
	        var dbvalue = member.Accessor.GetValue(dataStoreEntity, null);
	        var newvalue = member.Accessor.GetValue(updatingEntity, null);
	        if (dbvalue == null && newvalue == null) // No value
	            return;

	        // If we own the collection then we need to update the entities otherwise simple relationship update
	        if (!member.IsOwned)
	        {
	            if (newvalue == null)
	            {
	                member.Accessor.SetValue(dataStoreEntity, null, null);
	                return;
	            }

	            // do nothing if the key is already identical
	            if (IsKeyIdentical(context, newvalue, dbvalue))
	                return;

	            if (context.Entry(newvalue).State == EntityState.Detached)
	                context.Set(ObjectContext.GetObjectType(newvalue.GetType())).Attach(newvalue);

	            member.Accessor.SetValue(dataStoreEntity, newvalue, null);
	            context.Entry(newvalue).State = EntityState.Unchanged;
	            context.ReloadEntity(newvalue);
	        }
	        else
	        {
	            // Check if the same key, if so then update values on the entity
	            if (IsKeyIdentical(context, newvalue, dbvalue))
	                context.UpdateValuesWithConcurrencyCheck(newvalue, dbvalue);
	            else
	                member.Accessor.SetValue(dataStoreEntity, newvalue, null);

	            AttachCyclicNavigationProperty(context, dataStoreEntity, newvalue);

	            foreach (var childMember in member.Members)
	                RecursiveGraphUpdate(context, dbvalue, newvalue, childMember);
	        }
	    }

	    private static bool IsKeyIdentical(DbContext context, object newValue, object dbValue)
	    {
	        if (newValue == null || dbValue == null)
	            return false;

	        var keyFields = context.GetKeysFor(ObjectContext.GetObjectType(newValue.GetType()));
	        return CreateHash(keyFields, newValue) == CreateHash(keyFields, dbValue);
	    }

	    private static void ReloadEntity(this DbContext context, object entity)
	    {
	        if (GraphDiffConfiguration.ReloadAssociatedEntitiesWhenAttached)
	            context.Entry(entity).Reload();
	    }

	    private static string CreateHash(IEnumerable<PropertyInfo> keys, object entity)
        {
            // Create unique string representing the keys
            string code = "";

            foreach (var property in keys)
                code += "|" + property.GetValue(entity, null).GetHashCode();

            return code;
        }

	    #endregion

		#region Extensions

		// attaches the navigation property of a child back to its parent (if exists)
	    private static void AttachCyclicNavigationProperty(this IObjectContextAdapter context, object parent, object child)
		{
			if (parent == null || child == null)
				return;

			var parentType = ObjectContext.GetObjectType(parent.GetType());
			var childType = ObjectContext.GetObjectType(child.GetType());

	        var navigationProperties = context.ObjectContext.MetadataWorkspace
	                .GetItems<EntityType>(DataSpace.OSpace)
	                .Single(p => p.FullName == childType.FullName)
	                .NavigationProperties;

	        var parentNavigationProperty = navigationProperties
	                .Where(navigation => navigation.TypeUsage.EdmType.Name == parentType.Name)
	                .Select(navigation => childType.GetProperty(navigation.Name))
	                .FirstOrDefault();

            if (parentNavigationProperty != null)
                parentNavigationProperty.SetValue(child, parent, null);
		}

	    private static T FindEntityMatching<T>(this DbContext context, T entity, params string[] includes) where T : class
		{
            // attach includes to IQueryable
			var query = context.Set<T>().AsQueryable();
			foreach (var include in includes)
				query = query.Include(include);

            // get key properties of T
			var keyProperties = context.GetKeysFor(typeof(T)).ToList();

			// Run the find operation
			ParameterExpression parameter = Expression.Parameter(typeof(T));
			Expression expression = Expression.Equal(Expression.Property(parameter, keyProperties[0]), Expression.Constant(keyProperties[0].GetValue(entity, null)));
			for (int i = 1; i < keyProperties.Count; i++)
			{
				expression = Expression.And(expression,
                    Expression.Equal(Expression.Property(parameter, keyProperties[i]), Expression.Constant(keyProperties[i].GetValue(entity, null))));
			}
			var lambda = Expression.Lambda<Func<T, bool>>(expression, parameter);
            return query.SingleOrDefault(lambda);
		}

        private static void UpdateValuesWithConcurrencyCheck<T>(this DbContext context, T from, T to) where T : class
	    {
            context.EnsureConcurrency(from, to);
            context.Entry(to).CurrentValues.SetValues(from);
	    }

        // Ensures concurrency properties are checked (manual at the moment.. todo)
	    private static void EnsureConcurrency<T>(this IObjectContextAdapter db, T from, T to)
        {
            // get concurrency properties of T
            var entityType = ObjectContext.GetObjectType(from.GetType());
            var metadata = db.ObjectContext.MetadataWorkspace;

            var objType = metadata.GetItems<EntityType>(DataSpace.OSpace).Single(p => p.FullName == entityType.FullName);

            // need internal string, code smells bad.. any better way to do this?
            var cTypeName = (string) objType.GetType()
                    .GetProperty("CSpaceTypeName", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(objType, null);

            var conceptualType = metadata.GetItems<EntityType>(DataSpace.CSpace).Single(p => p.FullName == cTypeName);
            var concurrencyProperties = conceptualType.Members
                    .Where(member => member.TypeUsage.Facets.Any(facet => facet.Name == "ConcurrencyMode" && (ConcurrencyMode) facet.Value == ConcurrencyMode.Fixed))
                    .Select(member => entityType.GetProperty(member.Name))
                    .ToList();

            // Check if concurrency properties are equal
            // TODO EF should do this automatically should it not?
            foreach (PropertyInfo concurrencyProp in concurrencyProperties)
            {
                // if is byte[] use array comparison, else equals().
                if ((concurrencyProp.PropertyType == typeof(byte[]) && !((byte[])concurrencyProp.GetValue(from, null)).SequenceEqual((byte[])concurrencyProp.GetValue(to, null)))
                    || concurrencyProp.GetValue(from, null).Equals(concurrencyProp.GetValue(to, null)))
                {
                    throw new DbUpdateConcurrencyException(String.Format("{0} failed optimistic concurrency", concurrencyProp.Name));
                }  
            }
        }

		// Gets the primary key fields for an entity type.
	    private static List<PropertyInfo> GetKeysFor(this DbContext db, Type entityType)
		{
            var metadata = ((IObjectContextAdapter)db).ObjectContext.MetadataWorkspace;
            var type = metadata.GetItems<EntityType>(DataSpace.OSpace).Single(p => p.FullName == entityType.FullName);
            return type.KeyMembers.Select(k => entityType.GetProperty(k.Name)).ToList();
		}

		#endregion

	}
}
