fn main() -> Result<(), Box<dyn std::error::Error>> {
    println!("cargo:rerun-if-changed=build.rs");
    println!("cargo:rerun-if-changed=windows.manifest");
    if std::env::var_os("CARGO_CFG_WINDOWS").is_some() {
        // GetWindowSubclass is exported by Common Controls v6, selected by the
        // executable's activation manifest before Windows resolves its imports.
        embed_manifest::embed_manifest_file("windows.manifest")?;
    }
    Ok(())
}
