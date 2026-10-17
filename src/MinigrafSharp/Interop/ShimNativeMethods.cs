using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MinigrafSharp.Native;

namespace MinigrafSharp.Interop;

/// <summary>
/// The owned shim ABI (<c>native/include/minigrafsharp_shim.h</c>, shim
/// ABI 0.1.0: the 12 M3.1 exports plus the M3.2b write-transaction and
/// structured-error exports). Wrapped surface:
/// <see cref="Native.SafeMgsDatabaseHandle"/> for databases, plain nint for
/// prepared queries (freed under the parent's per-database gate, never through
/// a <c>SafeHandle</c>), <see cref="NativeUtf8"/> for string copies, and
/// <c>MgsStringFree</c> for owned shim strings exactly once.
/// Loaded only via the combined assembly resolver routing
/// <see cref="Native.ShimNativeLoader.LibraryName"/>, after identity
/// verification; there is no alternate load path (ADR 0002 hard invariant 5).
/// </summary>
internal static partial class ShimNativeMethods
{
    private const string LibraryName = Native.ShimNativeLoader.LibraryName;

    // C ABI: const char *mgs_shim_identity(void);
    // Static borrowed token; read by the loader directly via GetExport, not
    // through this import. Declared here for ABI completeness of the 12-function
    // closure; no production path calls it through P/Invoke.
    [LibraryImport(
        LibraryName,
        EntryPoint = "mgs_shim_identity")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint MgsShimIdentity();

    // C ABI: MgsDb *mgs_open(const char *path);
    // Returns NULL on error. Ownership transfers to the caller.
    [LibraryImport(
        LibraryName,
        EntryPoint = "mgs_open",
        StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint MgsOpen(string path);

    // C ABI: MgsDb *mgs_open_in_memory(void);
    // Returns NULL on error. Ownership transfers to the caller.
    [LibraryImport(
        LibraryName,
        EntryPoint = "mgs_open_in_memory")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint MgsOpenInMemory();

    // C ABI: void mgs_close(MgsDb *handle);
    // Called exactly once per owned handle via SafeHandle.ReleaseHandle.
    [LibraryImport(
        LibraryName,
        EntryPoint = "mgs_close")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void MgsClose(nint handle);

    // C ABI: void mgs_string_free(char *s);
    // Releases owned strings from mgs_execute / mgs_prepared_execute /
    // mgs_prepared_slot_names. NULL safe.
    [LibraryImport(
        LibraryName,
        EntryPoint = "mgs_string_free")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void MgsStringFree(nint s);

    // C ABI: const char *mgs_last_error(const MgsDb *handle);
    // Borrowed pointer: copy immediately, never free, valid only until the
    // next call on the same handle. May return NULL.
    [LibraryImport(
        LibraryName,
        EntryPoint = "mgs_last_error")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint MgsLastError(
        SafeMgsDatabaseHandle handle);

    // C ABI: const char *mgs_prepared_last_error(const MgsPreparedQuery *prepared);
    // Borrowed pointer with the same discipline as mgs_last_error.
    [LibraryImport(
        LibraryName,
        EntryPoint = "mgs_prepared_last_error")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint MgsPreparedLastError(nint prepared);

    // C ABI: char *mgs_execute(MgsDb *handle, const char *datalog);
    // Owned result JSON (official shapes) or NULL on error.
    [LibraryImport(
        LibraryName,
        EntryPoint = "mgs_execute",
        StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint MgsExecute(
        SafeMgsDatabaseHandle handle,
        string datalog);

    // C ABI: MgsPreparedQuery *mgs_prepare(MgsDb *handle, const char *datalog);
    // NULL on error; diagnostic stored on the database handle. Ownership
    // transfers to the caller on success.
    [LibraryImport(
        LibraryName,
        EntryPoint = "mgs_prepare",
        StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint MgsPrepare(
        SafeMgsDatabaseHandle handle,
        string datalog);

    // C ABI: char *mgs_prepared_slot_names(const MgsPreparedQuery *prepared);
    // Owned JSON string array (sorted, deduplicated) or NULL on error.
    [LibraryImport(
        LibraryName,
        EntryPoint = "mgs_prepared_slot_names")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint MgsPreparedSlotNames(nint prepared);

    // C ABI: char *mgs_prepared_execute(MgsPreparedQuery *prepared, const char *bindings_json);
    // Owned result JSON (official shapes) or NULL on error.
    [LibraryImport(
        LibraryName,
        EntryPoint = "mgs_prepared_execute",
        StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint MgsPreparedExecute(
        nint prepared,
        string bindingsJson);

    // C ABI: void mgs_prepared_query_free(MgsPreparedQuery *prepared);
    // Exactly once per owned handle; NULL safe. Called under the parent
    // database's gate only.
    [LibraryImport(
        LibraryName,
        EntryPoint = "mgs_prepared_query_free")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void MgsPreparedQueryFree(nint prepared);
    // C ABI: MgsWriteTx *mgs_begin_write(MgsDb *handle);
    // NULL on failure; the structured diagnostic is on the database handle.
    // Ownership transfers to the caller on success.
    [LibraryImport(
        LibraryName,
        EntryPoint = "mgs_begin_write")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint MgsBeginWrite(SafeMgsDatabaseHandle handle);

    // C ABI: char *mgs_tx_execute(MgsWriteTx *tx, const char *datalog);
    // Owned result JSON or NULL on error (structured diagnostic on the tx).
    [LibraryImport(
        LibraryName,
        EntryPoint = "mgs_tx_execute",
        StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint MgsTxExecute(nint tx, string datalog);

    // C ABI: int mgs_tx_commit(MgsWriteTx *tx);
    // 0 on success, -1 on failure (diagnostic retained on the wrapper).
    [LibraryImport(
        LibraryName,
        EntryPoint = "mgs_tx_commit")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int MgsTxCommit(nint tx);

    // C ABI: void mgs_tx_rollback(MgsWriteTx *tx);
    // Infallible terminal operation.
    [LibraryImport(
        LibraryName,
        EntryPoint = "mgs_tx_rollback")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void MgsTxRollback(nint tx);

    // C ABI: int mgs_write_tx_free(MgsWriteTx *tx);
    // Frees the wrapper (rolling back an open inner transaction on the owner
    // thread). Called exactly once per owned wrapper, under the transaction's
    // lock, from the owner thread.
    [LibraryImport(
        LibraryName,
        EntryPoint = "mgs_write_tx_free")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int MgsWriteTxFree(nint tx);

    // C ABI: char *mgs_last_error_json(const MgsDb *handle);
    // Owned structured error JSON or NULL when there is no error.
    [LibraryImport(
        LibraryName,
        EntryPoint = "mgs_last_error_json")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint MgsLastErrorJson(SafeMgsDatabaseHandle handle);

    // C ABI: char *mgs_tx_last_error_json(const MgsWriteTx *tx);
    // Owned structured error JSON or NULL when there is no error.
    [LibraryImport(
        LibraryName,
        EntryPoint = "mgs_tx_last_error_json")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint MgsTxLastErrorJson(nint tx);
    // C ABI: MgsDb *mgs_open_with_options(const char *path, const char *options_json, char **error_json_out);
    // NULL on failure; when error_json_out is non-NULL it receives an owned
    // structured-error JSON string (NULL on success), freed by the caller.
    [LibraryImport(
        LibraryName,
        EntryPoint = "mgs_open_with_options",
        StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint MgsOpenWithOptions(
        string path,
        string optionsJson,
        out nint errorJson);

    // C ABI: MgsDb *mgs_open_in_memory_with_options(const char *options_json, char **error_json_out);
    // Same error contract.
    [LibraryImport(
        LibraryName,
        EntryPoint = "mgs_open_in_memory_with_options",
        StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint MgsOpenInMemoryWithOptions(
        string optionsJson,
        out nint errorJson);
    // C ABI: char *mgs_prepared_last_error_json(const MgsPreparedQuery *prepared);
    // Owned structured-error JSON or NULL when there is no error. Cleared by
    // the next successful prepared operation; failures replace earlier ones.
    [LibraryImport(
        LibraryName,
        EntryPoint = "mgs_prepared_last_error_json")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint MgsPreparedLastErrorJson(nint prepared);
}
