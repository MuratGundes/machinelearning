// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.ML.Runtime.Data;
using Microsoft.ML.Runtime.Internal.Utilities;

namespace Microsoft.ML.Runtime.Api
{
    /// <summary>
    /// This interface is an <see cref="IRow"/> with 'strongly typed' binding.
    /// It can populate the user-supplied object's fields with the values of the current row.
    /// </summary>
    /// <typeparam name="TRow">The user-defined type that is being populated while cursoring.</typeparam>
    public interface IRowReadableAs<TRow> : IRow
        where TRow : class
    {
        /// <summary>
        /// Populates the fields of the user-supplied <paramref name="row"/> object with the values of the current row.
        /// </summary>
        /// <param name="row">The row object. Cannot be null.</param>
        void FillValues(TRow row);
    }

    /// <summary>
    /// This interface is an <see cref="IRow"/> with 'strongly typed' binding.
    /// It can accept values of type <typeparamref name="TRow"/> and present the value as a row.
    /// </summary>
    /// <typeparam name="TRow">The user-defined type that provides the values while cursoring.</typeparam>
    public interface IRowBackedBy<TRow> : IRow
        where TRow : class
    {
        /// <summary>
        /// Accepts the fields of the user-supplied <paramref name="row"/> object and publishes the instance as a row.
        /// If the row is accessed prior to any object being set, then the data accessors on the row should throw.
        /// </summary>
        /// <param name="row">The row object. Cannot be <c>null</c>.</param>
        void ExtractValues(TRow row);
    }

    /// <summary>
    /// This interface provides cursoring through a <see cref="IDataView"/> via a 'strongly typed' binding.
    /// It can populate the user-supplied object's fields with the values of the current row.
    /// </summary>
    /// <typeparam name="TRow">The user-defined type that is being populated while cursoring.</typeparam>
    public interface IRowCursor<TRow> : IRowReadableAs<TRow>, ICursor
        where TRow : class
    {
    }

    /// <summary>
    /// This interface allows to create strongly typed cursors over a <see cref="IDataView"/>.
    /// </summary>
    /// <typeparam name="TRow">The user-defined type that is being populated while cursoring.</typeparam>
    public interface ICursorable<TRow>
        where TRow : class
    {
        /// <summary>
        /// Get a new cursor.
        /// </summary>
        IRowCursor<TRow> GetCursor();

        /// <summary>
        /// Get a new randomized cursor.
        /// </summary>
        /// <param name="randomSeed">The random seed to use.</param>
        IRowCursor<TRow> GetRandomizedCursor(int randomSeed);
    }

    /// <summary>
    /// Implementation of the strongly typed Cursorable.
    /// Similarly to the 'DataView{T}, this class uses IL generation to create the 'poke' methods that
    /// write directly into the fields of the user-defined type.
    /// </summary>
    internal sealed class TypedCursorable<TRow> : ICursorable<TRow>
        where TRow : class
    {
        private readonly IHost _host;

        // The underlying DataView.
        private readonly IDataView _data;

        // Potentially, this can be a subset of the columns defined in the user-provided schema.
        private readonly InternalSchemaDefinition.Column[] _columns;

        // The indices of the columns to request from the underlying DataView. Parallel to _columns.
        private readonly int[] _columnIndices;

        // IL-generated methods to set the fields of the T object. Parallel to _columns.
        private readonly Delegate[] _pokes;

        // IL-generated methods to fetch the fields of the T object, in the event that it is a
        // type of object that could potentially be re-used, i.e., it is a vector type. If not
        // vector typed this will be null. Parallel to _columns.
        private readonly Delegate[] _peeks;

        private TypedCursorable(IHostEnvironment env, IDataView data, bool ignoreMissingColumns, InternalSchemaDefinition schemaDefn)
        {
            Contracts.AssertValue(env, "env");
            _host = env.Register("TypedCursorable");
            _host.AssertValue(data);
            _host.AssertValue(schemaDefn);

            _data = data;
            // Get column indices. Throw if there are missing columns (optionally, ignore them).
            var acceptedCols = new List<InternalSchemaDefinition.Column>();
            var indices = new List<int>();
            foreach (var col in schemaDefn.Columns)
            {
                int colIndex;
                if (!_data.Schema.TryGetColumnIndex(col.ColumnName, out colIndex))
                {
                    if (ignoreMissingColumns)
                        continue;
                    throw _host.Except("Column '{0}' not found in the data view", col.ColumnName);
                }
                var realColType = _data.Schema.GetColumnType(colIndex);
                if (!IsCompatibleType(realColType, col.MemberInfo))
                {
                    throw _host.Except(
                        "Can't bind the IDataView column '{0}' of type '{1}' to field or property '{2}' of type '{3}'.",
                        col.ColumnName, realColType, col.MemberInfo.Name, col.FieldOrPropertyType.FullName);
                }

                acceptedCols.Add(col);
                indices.Add(colIndex);
            }
            _columns = acceptedCols.ToArray();
            _columnIndices = indices.ToArray();
            _host.Assert(_columns.Length == _columnIndices.Length);

            int n = _columns.Length;
            _pokes = new Delegate[n];
            _peeks = new Delegate[n];
            var schema = _data.Schema;
            for (int i = 0; i < n; i++)
            {
                if (_columns[i].ColumnType.IsVector)
                    _peeks[i] = ApiUtils.GeneratePeek<TypedCursorable<TRow>, TRow>(_columns[i]);
                _pokes[i] = ApiUtils.GeneratePoke<TypedCursorable<TRow>, TRow>(_columns[i]);
            }
        }

        /// <summary>
        /// Returns whether the column type <paramref name="colType"/> can be bound to field <paramref name="memberInfo"/>.
        /// They must both be vectors or scalars, and the raw data kind should match.
        /// </summary>
        private static bool IsCompatibleType(ColumnType colType, MemberInfo memberInfo)
        {
            InternalSchemaDefinition.GetVectorAndKind(memberInfo, out bool isVector, out DataKind kind);
            if (isVector)
                return colType.IsVector && colType.ItemType.RawKind == kind;
            else
                return !colType.IsVector && colType.RawKind == kind;
        }

        /// <summary>
        /// Create and return a new cursor.
        /// </summary>
        public IRowCursor<TRow> GetCursor()
        {
            return GetCursor(x => false);
        }

        /// <summary>
        /// Create and return a new randomized cursor.
        /// </summary>
        /// <param name="randomSeed">The random seed to use.</param>
        public IRowCursor<TRow> GetRandomizedCursor(int randomSeed)
        {
            return GetCursor(x => false, randomSeed);
        }

        public IRowReadableAs<TRow> GetRow(IRow input)
        {
            return new TypedRow(this, input);
        }

        /// <summary>
        /// Create a new cursor with additional active columns.
        /// </summary>
        /// <param name="additionalColumnsPredicate">Predicate that denotes which additional columns to include in the cursor,
        /// in addition to the columns that are needed for populating the <typeparamref name="TRow"/> object.</param>
        /// <param name="randomSeed">The random seed to use. If <c>null</c>, the cursor will be non-randomized.</param>
        public IRowCursor<TRow> GetCursor(Func<int, bool> additionalColumnsPredicate, int? randomSeed = null)
        {
            _host.CheckValue(additionalColumnsPredicate, nameof(additionalColumnsPredicate));

            IRandom rand = randomSeed.HasValue ? RandomUtils.Create(randomSeed.Value) : null;

            var cursor = _data.GetRowCursor(GetDependencies(additionalColumnsPredicate), rand);
            return new TypedCursor(this, cursor);
        }

        public Func<int, bool> GetDependencies(Func<int, bool> additionalColumnsPredicate)
        {
            return col => _columnIndices.Contains(col) || additionalColumnsPredicate(col);
        }

        /// <summary>
        /// Create a set of cursors with additional active columns.
        /// </summary>
        /// <param name="consolidator">The consolidator for the original row cursors</param>
        /// <param name="additionalColumnsPredicate">Predicate that denotes which additional columns to include in the cursor,
        /// in addition to the columns that are needed for populating the <typeparamref name="TRow"/> object.</param>
        /// <param name="n">Number of cursors to create</param>
        /// <param name="rand">Random generator to use</param>
        public IRowCursor<TRow>[] GetCursorSet(out IRowCursorConsolidator consolidator,
            Func<int, bool> additionalColumnsPredicate, int n, IRandom rand)
        {
            _host.CheckValue(additionalColumnsPredicate, nameof(additionalColumnsPredicate));
            _host.CheckValueOrNull(rand);

            Func<int, bool> inputPredicate = col => _columnIndices.Contains(col) || additionalColumnsPredicate(col);
            var inputs = _data.GetRowCursorSet(out consolidator, inputPredicate, n, rand);
            _host.AssertNonEmpty(inputs);

            if (inputs.Length == 1 && n > 1)
                inputs = DataViewUtils.CreateSplitCursors(out consolidator, _host, inputs[0], n);
            _host.AssertNonEmpty(inputs);

            return inputs
                .Select(rc => (IRowCursor<TRow>)(new TypedCursor(this, rc)))
                .ToArray();
        }

        /// <summary>
        /// Create a Cursorable object on a given data view.
        /// </summary>
        /// <param name="env">Host enviroment.</param>
        /// <param name="data">The underlying data view.</param>
        /// <param name="ignoreMissingColumns">Whether to ignore missing columns in the data view.</param>
        /// <param name="schemaDefinition">The optional user-provided schema.</param>
        /// <returns>The constructed Cursorable.</returns>
        public static TypedCursorable<TRow> Create(IHostEnvironment env, IDataView data, bool ignoreMissingColumns, SchemaDefinition schemaDefinition)
        {
            Contracts.AssertValue(env);
            env.AssertValue(data);
            env.AssertValueOrNull(schemaDefinition);

            var outSchema = schemaDefinition == null
                ? InternalSchemaDefinition.Create(typeof(TRow), SchemaDefinition.Direction.Write)
                : InternalSchemaDefinition.Create(typeof(TRow), schemaDefinition);

            return new TypedCursorable<TRow>(env, data, ignoreMissingColumns, outSchema);
        }

        private abstract class TypedRowBase : IRowReadableAs<TRow>
        {
            protected readonly IChannel Ch;
            private readonly IRow _input;
            private readonly Action<TRow>[] _setters;

            public long Batch => _input.Batch;

            public long Position => _input.Position;

            public ISchema Schema => _input.Schema;

            public TypedRowBase(TypedCursorable<TRow> parent, IRow input, string channelMessage)
            {
                Contracts.AssertValue(parent);
                Contracts.AssertValue(parent._host);
                Ch = parent._host.Start(channelMessage);
                Ch.AssertValue(input);

                _input = input;

                int n = parent._pokes.Length;
                Ch.Assert(n == parent._columns.Length);
                Ch.Assert(n == parent._columnIndices.Length);
                _setters = new Action<TRow>[n];
                for (int i = 0; i < n; i++)
                    _setters[i] = GenerateSetter(_input, parent._columnIndices[i], parent._columns[i], parent._pokes[i], parent._peeks[i]);
            }

            public ValueGetter<UInt128> GetIdGetter()
            {
                return _input.GetIdGetter();
            }

            private Action<TRow> GenerateSetter(IRow input, int index, InternalSchemaDefinition.Column column, Delegate poke, Delegate peek)
            {
                var colType = input.Schema.GetColumnType(index);
                var fieldType = column.OutputType;
                var genericType = fieldType;
                Func<IRow, int, Delegate, Delegate, Action<TRow>> del;
                if (fieldType.IsArray)
                {
                    Ch.Assert(colType.IsVector);
                    // VBuffer<ReadOnlyMemory<char>> -> String[]
                    if (fieldType.GetElementType() == typeof(string))
                    {
                        Ch.Assert(colType.ItemType.IsText);
                        return CreateConvertingVBufferSetter<ReadOnlyMemory<char>, string>(input, index, poke, peek, x => x.ToString());
                    }

                    // VBuffer<T> -> T[]
                    if (fieldType.GetElementType().IsGenericType && fieldType.GetElementType().GetGenericTypeDefinition() == typeof(Nullable<>))
                        Ch.Assert(colType.ItemType.RawType == Nullable.GetUnderlyingType(fieldType.GetElementType()));
                    else
                        Ch.Assert(colType.ItemType.RawType == fieldType.GetElementType());
                    del = CreateDirectVBufferSetter<int>;
                    genericType = fieldType.GetElementType();
                }
                else if (colType.IsVector)
                {
                    // VBuffer<T> -> VBuffer<T>
                    // REVIEW: Do we care about accomodating VBuffer<string> -> VBuffer<ReadOnlyMemory<char>>?
                    Ch.Assert(fieldType.IsGenericType);
                    Ch.Assert(fieldType.GetGenericTypeDefinition() == typeof(VBuffer<>));
                    Ch.Assert(fieldType.GetGenericArguments()[0] == colType.ItemType.RawType);
                    del = CreateVBufferToVBufferSetter<int>;
                    genericType = colType.ItemType.RawType;
                }
                else if (colType.IsPrimitive)
                {
                    if (fieldType == typeof(string))
                    {
                        // ReadOnlyMemory<char> -> String
                        Ch.Assert(colType.IsText);
                        Ch.Assert(peek == null);
                        return CreateConvertingActionSetter<ReadOnlyMemory<char>, string>(input, index, poke, x => x.ToString());
                    }

                    // T -> T
                    if (fieldType.IsGenericType && fieldType.GetGenericTypeDefinition() == typeof(Nullable<>))
                        Ch.Assert(colType.RawType == Nullable.GetUnderlyingType(fieldType));
                    else
                        Ch.Assert(colType.RawType == fieldType);

                    del = CreateDirectSetter<int>;
                }
                else
                {
                    // REVIEW: Is this even possible?
                    throw Ch.ExceptNotImpl("Type '{0}' is not yet supported.", column.OutputType.FullName);
                }
                MethodInfo meth = del.GetMethodInfo().GetGenericMethodDefinition().MakeGenericMethod(genericType);
                return (Action<TRow>)meth.Invoke(this, new object[] { input, index, poke, peek });
            }

            // REVIEW: The converting getter invokes a type conversion delegate on every call, so it's inherently slower
            // than the 'direct' getter. We don't have good indication of this to the user, and the selection
            // of affected types is pretty arbitrary (signed integers and bools, but not uints and floats).
            private Action<TRow> CreateConvertingVBufferSetter<TSrc, TDst>(IRow input, int col, Delegate poke, Delegate peek, Func<TSrc, TDst> convert)
            {
                var getter = input.GetGetter<VBuffer<TSrc>>(col);
                var typedPoke = poke as Poke<TRow, TDst[]>;
                var typedPeek = peek as Peek<TRow, TDst[]>;
                Contracts.AssertValue(typedPoke);
                Contracts.AssertValue(typedPeek);
                VBuffer<TSrc> value = default;
                TDst[] buf = null;
                return row =>
                {
                    getter(ref value);
                    typedPeek(row, Position, ref buf);
                    if (Utils.Size(buf) != value.Length)
                        buf = new TDst[value.Length];
                    foreach (var pair in value.Items(true))
                        buf[pair.Key] = convert(pair.Value);

                    typedPoke(row, buf);
                };
            }

            private Action<TRow> CreateDirectVBufferSetter<TDst>(IRow input, int col, Delegate poke, Delegate peek)
            {
                var getter = input.GetGetter<VBuffer<TDst>>(col);
                var typedPoke = poke as Poke<TRow, TDst[]>;
                var typedPeek = peek as Peek<TRow, TDst[]>;
                Contracts.AssertValue(typedPoke);
                Contracts.AssertValue(typedPeek);
                VBuffer<TDst> value = default(VBuffer<TDst>);
                TDst[] buf = null;
                return row =>
                {
                    typedPeek(row, Position, ref buf);
                    value = new VBuffer<TDst>(0, buf, value.Indices);
                    getter(ref value);
                    if (value.Length == Utils.Size(buf) && value.IsDense)
                    {
                        // In this case, value.Values alone is enough to represent the vector.
                        // Otherwise, we are either sparse (and need densifying), or value.Values is too large,
                        // and we need to truncate.
                        buf = value.Values;
                    }
                    else
                    {
                        buf = new TDst[value.Length];

                        if (value.IsDense)
                            Array.Copy(value.Values, buf, value.Length);
                        else
                        {
                            foreach (var pair in value.Items(true))
                                buf[pair.Key] = pair.Value;
                        }
                    }

                    typedPoke(row, buf);
                };
            }

            private static Action<TRow> CreateConvertingActionSetter<TSrc, TDst>(IRow input, int col, Delegate poke, Func<TSrc, TDst> convert)
            {
                var getter = input.GetGetter<TSrc>(col);
                var typedPoke = poke as Poke<TRow, TDst>;
                Contracts.AssertValue(typedPoke);
                TSrc value = default;
                return row =>
                {
                    getter(ref value);
                    var toPoke = convert(value);
                    typedPoke(row, toPoke);
                };
            }

            private static Action<TRow> CreateDirectSetter<TDst>(IRow input, int col, Delegate poke, Delegate peek)
            {
                // Awkward to have a parameter that's always null, but slightly more convenient for generalizing the setter.
                Contracts.Assert(peek == null);
                var getter = input.GetGetter<TDst>(col);
                var typedPoke = poke as Poke<TRow, TDst>;
                Contracts.AssertValue(typedPoke);
                TDst value = default(TDst);
                return row =>
                {
                    getter(ref value);
                    typedPoke(row, value);
                };
            }

            private Action<TRow> CreateVBufferToVBufferSetter<TDst>(IRow input, int col, Delegate poke, Delegate peek)
            {
                var getter = input.GetGetter<VBuffer<TDst>>(col);
                var typedPoke = poke as Poke<TRow, VBuffer<TDst>>;
                var typedPeek = peek as Peek<TRow, VBuffer<TDst>>;
                Contracts.AssertValue(typedPoke);
                Contracts.AssertValue(typedPeek);
                VBuffer<TDst> value = default(VBuffer<TDst>);
                return row =>
                {
                    typedPeek(row, Position, ref value);
                    getter(ref value);
                    typedPoke(row, value);
                };
            }

            public virtual void FillValues(TRow row)
            {
                foreach (var setter in _setters)
                    setter(row);
            }

            public bool IsColumnActive(int col)
            {
                return _input.IsColumnActive(col);
            }

            public ValueGetter<TValue> GetGetter<TValue>(int col)
            {
                return _input.GetGetter<TValue>(col);
            }
        }

        private sealed class TypedRow : TypedRowBase
        {
            public TypedRow(TypedCursorable<TRow> parent, IRow input)
                : base(parent, input, "Row")
            {
            }
        }

        private sealed class TypedCursor : TypedRowBase, IRowCursor<TRow>
        {
            private readonly IRowCursor _input;
            private bool _disposed;

            public TypedCursor(TypedCursorable<TRow> parent, IRowCursor input)
                : base(parent, input, "Cursor")
            {
                _input = input;
            }

            public override void FillValues(TRow row)
            {
                Ch.Check(_input.State == CursorState.Good, "Can't fill values: the cursor is not active.");
                base.FillValues(row);
            }

            public CursorState State { get { return _input.State; } }

            public void Dispose()
            {
                if (!_disposed)
                {
                    _input.Dispose();
                    Ch.Dispose();
                    _disposed = true;
                }
            }

            public bool MoveNext()
            {
                return _input.MoveNext();
            }

            public bool MoveMany(long count)
            {
                return _input.MoveMany(count);
            }

            public ICursor GetRootCursor()
            {
                return _input.GetRootCursor();
            }
        }
    }

    /// <summary>
    /// Utility methods that facilitate strongly-typed cursoring.
    /// </summary>
    public static class CursoringUtils
    {
        private const string NeedEnvObsoleteMessage = "This method is obsolete. Please use the overload that takes an additional 'env' argument. An environment can be created via new LocalEnvironment().";

        /// <summary>
        /// Generate a strongly-typed cursorable wrapper of the <see cref="IDataView"/>.
        /// </summary>
        /// <typeparam name="TRow">The user-defined row type.</typeparam>
        /// <param name="data">The underlying data view.</param>
        /// <param name="env">The environment.</param>
        /// <param name="ignoreMissingColumns">Whether to ignore the case when a requested column is not present in the data view.</param>
        /// <param name="schemaDefinition">Optional user-provided schema definition. If it is not present, the schema is inferred from the definition of T.</param>
        /// <returns>The cursorable wrapper of <paramref name="data"/>.</returns>
        public static ICursorable<TRow> AsCursorable<TRow>(this IDataView data, IHostEnvironment env, bool ignoreMissingColumns = false,
            SchemaDefinition schemaDefinition = null)
            where TRow : class, new()
        {
            Contracts.CheckValue(env, nameof(env));
            env.CheckValue(data, nameof(data));
            env.CheckValueOrNull(schemaDefinition);

            return TypedCursorable<TRow>.Create(env, data, ignoreMissingColumns, schemaDefinition);
        }

        /// <summary>
        /// Generate a strongly-typed cursorable wrapper of the <see cref="IDataView"/>.
        /// </summary>
        /// <typeparam name="TRow">The user-defined row type.</typeparam>
        /// <param name="data">The underlying data view.</param>
        /// <param name="ignoreMissingColumns">Whether to ignore the case when a requested column is not present in the data view.</param>
        /// <param name="schemaDefinition">Optional user-provided schema definition. If it is not present, the schema is inferred from the definition of T.</param>
        /// <returns>The cursorable wrapper of <paramref name="data"/>.</returns>
        [Obsolete(NeedEnvObsoleteMessage)]
        public static ICursorable<TRow> AsCursorable<TRow>(this IDataView data, bool ignoreMissingColumns = false,
            SchemaDefinition schemaDefinition = null)
            where TRow : class, new()
        {
            // REVIEW: Take an env as a parameter.
            var env = new ConsoleEnvironment();
            return data.AsCursorable<TRow>(env, ignoreMissingColumns, schemaDefinition);
        }

        /// <summary>
        /// Convert an <see cref="IDataView"/> into a strongly-typed <see cref="IEnumerable{TRow}"/>.
        /// </summary>
        /// <typeparam name="TRow">The user-defined row type.</typeparam>
        /// <param name="data">The underlying data view.</param>
        /// <param name="env">The environment.</param>
        /// <param name="reuseRowObject">Whether to return the same object on every row, or allocate a new one per row.</param>
        /// <param name="ignoreMissingColumns">Whether to ignore the case when a requested column is not present in the data view.</param>
        /// <param name="schemaDefinition">Optional user-provided schema definition. If it is not present, the schema is inferred from the definition of T.</param>
        /// <returns>The <see cref="IEnumerable{TRow}"/> that holds the data in <paramref name="data"/>. It can be enumerated multiple times.</returns>
        public static IEnumerable<TRow> AsEnumerable<TRow>(this IDataView data, IHostEnvironment env, bool reuseRowObject,
            bool ignoreMissingColumns = false, SchemaDefinition schemaDefinition = null)
            where TRow : class, new()
        {
            Contracts.AssertValue(env);
            env.CheckValue(data, nameof(data));
            env.CheckValueOrNull(schemaDefinition);

            var engine = new PipeEngine<TRow>(env, data, ignoreMissingColumns, schemaDefinition);
            return engine.RunPipe(reuseRowObject);
        }

        /// <summary>
        /// Convert an <see cref="IDataView"/> into a strongly-typed <see cref="IEnumerable{TRow}"/>.
        /// </summary>
        /// <typeparam name="TRow">The user-defined row type.</typeparam>
        /// <param name="data">The underlying data view.</param>
        /// <param name="reuseRowObject">Whether to return the same object on every row, or allocate a new one per row.</param>
        /// <param name="ignoreMissingColumns">Whether to ignore the case when a requested column is not present in the data view.</param>
        /// <param name="schemaDefinition">Optional user-provided schema definition. If it is not present, the schema is inferred from the definition of T.</param>
        /// <returns>The <see cref="IEnumerable{TRow}"/> that holds the data in <paramref name="data"/>. It can be enumerated multiple times.</returns>
        [Obsolete(NeedEnvObsoleteMessage)]
        public static IEnumerable<TRow> AsEnumerable<TRow>(this IDataView data, bool reuseRowObject,
            bool ignoreMissingColumns = false, SchemaDefinition schemaDefinition = null)
            where TRow : class, new()
        {
            // REVIEW: Take an env as a parameter.
            var env = new ConsoleEnvironment();
            return data.AsEnumerable<TRow>(env, reuseRowObject, ignoreMissingColumns, schemaDefinition);
        }
    }
}
