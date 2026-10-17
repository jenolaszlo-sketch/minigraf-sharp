use std::{env, fs, path::Path};

fn str_field(root: &serde_json::Value, key: &str, source: &str) -> String {
    root.get(key)
        .and_then(|v| v.as_str())
        .filter(|s| !s.is_empty())
        .map(String::from)
        .unwrap_or_else(|| {
            panic!(
                "native/shim build: {source} is missing a non-empty string field '{key}'"
            )
        })
}

fn uint_field(root: &serde_json::Value, key: &str, source: &str) -> u64 {
    root.get(key).and_then(|v| v.as_u64()).unwrap_or_else(|| {
        panic!("native/shim build: {source} is missing a u64 field '{key}'")
    })
}

fn main() {
    println!("cargo:rerun-if-changed=identity.json");
    println!("cargo:rerun-if-changed=../upstream.json");

    let manifest = env::var("CARGO_MANIFEST_DIR")
        .unwrap_or_else(|e| panic!("native/shim build: no CARGO_MANIFEST_DIR: {e}"));
    let root = Path::new(&manifest);

    let upstream_text = fs::read_to_string(root.join("../upstream.json")).unwrap_or_else(|e| {
        panic!("native/shim build: cannot read ../upstream.json: {e}")
    });
    let identity_text = fs::read_to_string(root.join("identity.json"))
        .unwrap_or_else(|e| panic!("native/shim build: cannot read identity.json: {e}"));

    let upstream: serde_json::Value = serde_json::from_str(&upstream_text)
        .unwrap_or_else(|e| panic!("native/shim build: ../upstream.json parse failure: {e}"));
    let identity: serde_json::Value = serde_json::from_str(&identity_text)
        .unwrap_or_else(|e| panic!("native/shim build: identity.json parse failure: {e}"));

    let engine = upstream
        .get("minigraf")
        .unwrap_or_else(|| panic!("native/shim build: ../upstream.json is missing 'minigraf'"));
    let shim_abi = str_field(&identity, "shim_abi", "identity.json");
    let source_revision = uint_field(&identity, "source_revision", "identity.json");
    let core_version = str_field(engine, "version", "../upstream.json:minigraf");
    let core_commit = str_field(engine, "commit", "../upstream.json:minigraf");

    if core_commit.len() != 40 || !core_commit.chars().all(|c| c.is_ascii_hexdigit()) {
        panic!(
            "native/shim build: core commit '{}' is not a 40-char hex revision",
            core_commit
        );
    }

    // Byte-stable token; key order is fixed by proposal docs/m3-1a-shim-abi-proposal.md.
    let token = format!(
        "{{\"shim_abi\":{},\"shim_source_revision\":{},\"core_version\":{},\"core_commit\":{}}}",
        serde_json::to_string(&shim_abi).unwrap(),
        source_revision,
        serde_json::to_string(&core_version).unwrap(),
        serde_json::to_string(&core_commit).unwrap(),
    );

    println!("cargo:rustc-env=MGS_SHIM_IDENTITY_TOKEN={token}");
}
