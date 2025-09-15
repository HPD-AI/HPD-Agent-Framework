use proc_macro::TokenStream;
use proc_macro2::{Ident, Span, TokenStream as TokenStream2};
use quote::{format_ident, quote};
use serde_json::json;
use std::collections::HashMap;
use syn::{
    parse_macro_input, Error, FnArg, ImplItem, ItemImpl, Lit, Meta, Pat, PatType, 
    ReturnType, Type, punctuated::Punctuated, token::Comma, Attribute, parse_quote,
};

/// Marker attribute for function parameters with descriptions
/// Usage: #[param(description = "The value to process")]
#[proc_macro_attribute]
pub fn param(_args: TokenStream, input: TokenStream) -> TokenStream {
    // This is a marker attribute - it doesn't transform the code
    // The actual processing happens in the #[hpd_plugin] macro
    input
}

/// Marker attribute for functions requiring permission
/// Usage: #[requires_permission]
#[proc_macro_attribute]
pub fn requires_permission(_args: TokenStream, input: TokenStream) -> TokenStream {
    // This is a marker attribute - it doesn't transform the code
    // The actual processing happens in the #[hpd_plugin] macro
    input
}

/// Main plugin macro - marks an impl block as containing AI functions
/// Usage: #[hpd_plugin("Plugin Name", "Plugin description")]
#[proc_macro_attribute]
pub fn hpd_plugin(args: TokenStream, input: TokenStream) -> TokenStream {
    let args = parse_macro_input!(args with Punctuated::<syn::Expr, Comma>::parse_terminated);
    let item_impl = parse_macro_input!(input as ItemImpl);

    match impl_hpd_plugin(args, item_impl) {
        Ok(tokens) => tokens.into(),
        Err(err) => err.to_compile_error().into(),
    }
}

/// AI function macro - marks individual methods for AI function registration
/// Usage: #[ai_function("Description", ...various options)]
#[proc_macro_attribute]
pub fn ai_function(args: TokenStream, input: TokenStream) -> TokenStream {
    let args = parse_macro_input!(args with Punctuated::<syn::Expr, Comma>::parse_terminated);
    let method = parse_macro_input!(input as syn::ImplItemFn);

    match impl_ai_function(args, method) {
        Ok(tokens) => tokens.into(),
        Err(err) => err.to_compile_error().into(),
    }
}

fn impl_hpd_plugin(args: Punctuated<syn::Expr, Comma>, mut item_impl: ItemImpl) -> Result<TokenStream2, Error> {
    let (plugin_name, plugin_description) = parse_plugin_args(&args)?;
    
    let struct_name = match &*item_impl.self_ty {
        Type::Path(type_path) => {
            if let Some(segment) = type_path.path.segments.last() {
                segment.ident.clone()
            } else {
                return Err(Error::new_spanned(&item_impl.self_ty, "Invalid struct name"));
            }
        }
        _ => return Err(Error::new_spanned(&item_impl.self_ty, "Expected struct type")),
    };

    // Find all methods marked with #[ai_function]
    let mut ai_functions = Vec::new();

    for item in &mut item_impl.items {
        if let ImplItem::Fn(method) = item {
            // Check if this method has #[ai_function] attribute
            let mut has_ai_function = false;
            let mut ai_function_args = None;
            
            method.attrs.retain(|attr| {
                if attr.path().is_ident("ai_function") {
                    has_ai_function = true;
                    // Parse the attribute arguments
                    if let Ok(args) = attr.parse_args_with(Punctuated::<syn::Expr, Comma>::parse_terminated) {
                        ai_function_args = Some(args);
                    }
                    false // Remove the attribute
                } else {
                    true // Keep other attributes
                }
            });

            if has_ai_function {
                let function_info = parse_ai_function_method(method, ai_function_args)?;
                ai_functions.push(function_info);
            }
        }
    }

    if ai_functions.is_empty() {
        return Err(Error::new_spanned(
            &item_impl,
            "No #[ai_function] methods found in plugin implementation",
        ));
    }

    // Generate the plugin registration code
    let registration_code = generate_plugin_registration(
        &struct_name,
        &plugin_name,
        &plugin_description,
        &ai_functions,
    )?;

    // Generate the registration code
    Ok(quote! {
        #item_impl
        
        #registration_code
        
        // Implement the Plugin trait - use conditional compilation for internal vs external
        #[cfg(any(test, feature = "internal"))]
        impl crate::agent::Plugin for #struct_name {
            fn register_functions(&self) {
                // Register functions only when explicitly called (not automatically)
                Self::register_with_agent();
            }
            
            fn get_plugin_info(&self) -> Vec<crate::agent::RustFunctionInfo> {
                let registration = Self::register_plugin();
                (&registration).into()
            }
        }
        
        #[cfg(not(any(test, feature = "internal")))]
        impl hpd_rust_agent::agent::Plugin for #struct_name {
            fn register_functions(&self) {
                // Register functions only when explicitly called (not automatically)
                Self::register_with_agent();
            }
            
            fn get_plugin_info(&self) -> Vec<hpd_rust_agent::agent::RustFunctionInfo> {
                let registration = Self::register_plugin();
                (&registration).into()
            }
        }
    })
}

fn impl_ai_function(args: Punctuated<syn::Expr, Comma>, method: syn::ImplItemFn) -> Result<TokenStream2, Error> {
    // For standalone #[ai_function] usage (outside of #[hpd_plugin])
    // This will just preserve the method as-is since the real processing happens in hpd_plugin
    Ok(quote! { #method })
}

fn parse_plugin_args(args: &Punctuated<syn::Expr, Comma>) -> Result<(String, String), Error> {
    let mut plugin_name = None;
    let mut plugin_description = None;

    for (i, arg) in args.iter().enumerate() {
        if let syn::Expr::Lit(syn::ExprLit { lit: Lit::Str(lit_str), .. }) = arg {
            match i {
                0 => plugin_name = Some(lit_str.value()),
                1 => plugin_description = Some(lit_str.value()),
                _ => return Err(Error::new_spanned(arg, "Too many string arguments")),
            }
        } else {
            return Err(Error::new_spanned(arg, "Expected string literal"));
        }
    }

    let name = plugin_name.ok_or_else(|| Error::new(Span::call_site(), "Plugin name is required"))?;
    let description = plugin_description.unwrap_or_else(|| format!("Plugin: {}", name));

    Ok((name, description))
}

#[derive(Debug, Clone)]
struct AIFunctionInfo {
    method_name: String,
    function_name: Option<String>,
    description: String,
    parameters: Vec<ParameterInfo>,
    return_type: String,
    is_async: bool,
    required_permissions: Vec<String>,
    requires_permission: bool,
    conditional_expression: Option<String>,
}

#[derive(Debug, Clone)]
struct ParameterInfo {
    name: String,
    param_type: String,
    description: String,
    has_default_value: bool,
    default_value: Option<String>,
    conditional_expression: Option<String>,
    is_nullable: bool,
}

fn parse_ai_function_method(
    method: &syn::ImplItemFn,
    args: Option<Punctuated<syn::Expr, Comma>>,
) -> Result<AIFunctionInfo, Error> {
    let method_name = method.sig.ident.to_string();
    let is_async = method.sig.asyncness.is_some();
    
    // Parse return type
    let return_type = match &method.sig.output {
        ReturnType::Default => "()".to_string(),
        ReturnType::Type(_, ty) => quote!(#ty).to_string(),
    };

    // Parse function arguments from macro attributes
    let mut function_name = None;
    let mut description = String::new();
    let mut required_permissions = Vec::new();
    let mut requires_permission = false;
    let mut conditional_expression = None;

    // Check for #[requires_permission] attribute on the method
    for attr in &method.attrs {
        if attr.path().is_ident("requires_permission") {
            requires_permission = true;
        }
    }

    // Parse AI function description from args
    if let Some(args) = args {
        for (i, arg) in args.iter().enumerate() {
            match arg {
                syn::Expr::Lit(syn::ExprLit { lit: Lit::Str(lit_str), .. }) => {
                    if i == 0 {
                        description = lit_str.value();
                    }
                }
                syn::Expr::Assign(assign) => {
                    // Handle named arguments like name = "custom_name"
                    if let syn::Expr::Path(path) = assign.left.as_ref() {
                        if let Some(segment) = path.path.segments.last() {
                            if segment.ident == "name" {
                                if let syn::Expr::Lit(syn::ExprLit { lit: Lit::Str(lit_str), .. }) = assign.right.as_ref() {
                                    function_name = Some(lit_str.value());
                                }
                            }
                        }
                    }
                }
                _ => {}
            }
        }
    }

    // Use doc comments as fallback description
    if description.is_empty() {
        for attr in &method.attrs {
            if attr.path().is_ident("doc") {
                if let Ok(syn::Lit::Str(lit_str)) = attr.parse_args::<syn::Lit>() {
                    description = lit_str.value().trim().to_string();
                    break;
                }
            }
        }
    }

    // Parse method parameters
    let mut parameters = Vec::new();
    for input in &method.sig.inputs {
        if let FnArg::Typed(PatType { pat, ty, attrs, .. }) = input {
            if let Pat::Ident(pat_ident) = pat.as_ref() {
                let param_name = pat_ident.ident.to_string();
                let param_type = quote!(#ty).to_string();
                
                // Skip special framework parameters
                if param_name == "self" || param_type.contains("CancellationToken") 
                    || param_type.contains("ServiceProvider") {
                    continue;
                }
                
                let is_nullable = param_type.contains("Option<") || param_type.ends_with("?");
                
                // Parse #[param] attribute for description
                let mut param_description = format!("Parameter {}", param_name);
                for attr in attrs {
                    if attr.path().is_ident("param") {
                        if let Ok(meta) = attr.parse_args::<Meta>() {
                            if let Meta::NameValue(name_value) = meta {
                                if name_value.path.is_ident("description") {
                                    if let syn::Expr::Lit(syn::ExprLit { lit: Lit::Str(lit_str), .. }) = &name_value.value {
                                        param_description = lit_str.value();
                                    }
                                }
                            }
                        }
                    }
                }
                
                parameters.push(ParameterInfo {
                    name: param_name,
                    param_type,
                    description: param_description,
                    has_default_value: false, // TODO: Parse from attribute or type
                    default_value: None,
                    conditional_expression: None,
                    is_nullable,
                });
            }
        }
    }

    Ok(AIFunctionInfo {
        method_name,
        function_name,
        description,
        parameters,
        return_type,
        is_async,
        required_permissions,
        requires_permission,
        conditional_expression,
    })
}

fn generate_executor_registrations(
    struct_name: &Ident,
    functions: &[AIFunctionInfo],
) -> Vec<TokenStream2> {
    functions.iter().map(|func| {
        let func_name = func.function_name.as_ref().unwrap_or(&func.method_name);
        let method_ident = format_ident!("{}", func.method_name);
        
        // Generate parameter extraction code with proper error handling
        let param_extractions: Vec<TokenStream2> = func.parameters.iter().map(|param| {
            let param_name = format_ident!("{}", param.name);
            let param_name_str = &param.name;
            let param_type = &param.param_type;
            
            match param_type.as_str() {
                "f64" => quote! { 
                    let #param_name = args.get(#param_name_str)
                        .and_then(|v| v.as_f64())
                        .ok_or_else(|| format!("Missing or invalid parameter: {}", #param_name_str))?;
                },
                "i32" => quote! { 
                    let #param_name = args.get(#param_name_str)
                        .and_then(|v| v.as_i64())
                        .map(|v| v as i32)
                        .ok_or_else(|| format!("Missing or invalid parameter: {}", #param_name_str))?;
                },
                "u64" => quote! { 
                    let #param_name = args.get(#param_name_str)
                        .and_then(|v| v.as_u64())
                        .ok_or_else(|| format!("Missing or invalid parameter: {}", #param_name_str))?;
                },
                "bool" => quote! { 
                    let #param_name = args.get(#param_name_str)
                        .and_then(|v| v.as_bool())
                        .ok_or_else(|| format!("Missing or invalid parameter: {}", #param_name_str))?;
                },
                "String" => quote! { 
                    let #param_name = args.get(#param_name_str)
                        .and_then(|v| v.as_str())
                        .map(|s| s.to_string())
                        .ok_or_else(|| format!("Missing or invalid parameter: {}", #param_name_str))?;
                },
                _ => quote! { 
                    let #param_name = args.get(#param_name_str)
                        .ok_or_else(|| format!("Missing parameter: {}", #param_name_str))
                        .and_then(|v| serde_json::from_value(v.clone())
                            .map_err(|e| format!("Failed to parse parameter {}: {}", #param_name_str, e)))?;
                },
            }
        }).collect();
        
        let param_names: Vec<TokenStream2> = func.parameters.iter().map(|param| {
            let param_name = format_ident!("{}", param.name);
            quote! { #param_name }
        }).collect();
        
        let executor_code = if func.is_async {
            quote! {
                Box::pin(async move {
                    let args: std::collections::HashMap<String, serde_json::Value> = 
                        serde_json::from_str(&args_json)
                            .map_err(|e| format!("Failed to parse arguments: {}", e))?;
                    
                    #(#param_extractions)*
                    
                    let mut instance = #struct_name::default();
                    let result = instance.#method_ident(#(#param_names),*).await;
                    
                    // Serialize the result to JSON string
                    serde_json::to_string(&result)
                        .map_err(|e| format!("Failed to serialize result: {}", e))
                })
            }
        } else {
            quote! {
                Box::pin(async move {
                    let args: std::collections::HashMap<String, serde_json::Value> = 
                        serde_json::from_str(&args_json)
                            .map_err(|e| format!("Failed to parse arguments: {}", e))?;
                    
                    #(#param_extractions)*
                    
                    let mut instance = #struct_name::default();
                    let result = instance.#method_ident(#(#param_names),*);
                    
                    // Serialize the result to JSON string
                    serde_json::to_string(&result)
                        .map_err(|e| format!("Failed to serialize result: {}", e))
                })
            }
        };
        
        // Generate code that works for both internal and external usage
        quote! {
            // Register async executor with conditional paths
            #[cfg(any(test, feature = "internal"))]
            crate::plugins::register_async_executor(
                #func_name.to_string(),
                Box::new(move |args_json: String| {
                    #executor_code
                })
            );
            #[cfg(not(any(test, feature = "internal")))]
            hpd_rust_agent::plugins::register_async_executor(
                #func_name.to_string(),
                Box::new(move |args_json: String| {
                    #executor_code
                })
            );
        }
    }).collect()
}

fn generate_plugin_registration(
    struct_name: &Ident,
    plugin_name: &str,
    plugin_description: &str,
    functions: &[AIFunctionInfo],
) -> Result<TokenStream2, Error> {
    let plugin_registration_name = format_ident!("register_{}_plugin", struct_name.to_string().to_lowercase());
    let executor_registrations = generate_executor_registrations(struct_name, functions);
    
    // Generate JSON schema for each function
    let mut function_schemas = Vec::new();
    let mut function_wrappers = Vec::new();
    let mut function_registrations = Vec::new();
    let mut function_names = Vec::new();

    for func in functions {
        let func_name = func.function_name.as_ref().unwrap_or(&func.method_name);
        let method_ident = format_ident!("{}", func.method_name);
        let wrapper_name = format_ident!("{}_wrapper", func.method_name);
        
        // Collect function name for the function names list
        function_names.push(func_name);
        
        // Generate parameter schema
        let mut param_properties = serde_json::Map::new();
        let mut required_params = Vec::new();
        
        for param in &func.parameters {
            if !param.is_nullable && !param.has_default_value {
                required_params.push(param.name.clone());
            }
            
            let param_schema = json!({
                "type": rust_type_to_json_type(&param.param_type),
                "description": param.description
            });
            param_properties.insert(param.name.clone(), param_schema);
        }
        
        let function_schema = json!({
            "type": "function",
            "function": {
                "name": func_name,
                "description": func.description,
                "parameters": {
                    "type": "object",
                    "properties": param_properties,
                    "required": required_params
                }
            }
        });
        
        let schema_str = serde_json::to_string(&function_schema)
            .map_err(|e| Error::new(Span::call_site(), format!("Failed to serialize schema: {}", e)))?;
        
        function_schemas.push(quote! {
            (#func_name.to_string(), #schema_str.to_string())
        });

        // Generate wrapper function that can be called via FFI
        function_wrappers.push(quote! {
            #[no_mangle]
            pub extern "C" fn #wrapper_name(
                instance_ptr: *mut std::ffi::c_void,
                args_json: *const std::ffi::c_char
            ) -> *mut std::ffi::c_char {
                use std::ffi::{CStr, CString};
                
                if instance_ptr.is_null() || args_json.is_null() {
                    return std::ptr::null_mut();
                }
                
                let result = std::panic::catch_unwind(|| {
                    unsafe {
                        let instance = &*(instance_ptr as *const #struct_name);
                        let args_str = CStr::from_ptr(args_json).to_str().unwrap_or("{}");
                        let args: std::collections::HashMap<String, serde_json::Value> = 
                            serde_json::from_str(args_str).unwrap_or_default();
                        
                        // TODO: Add proper parameter extraction and method calling
                        let result = serde_json::json!({"status": "success", "result": null});
                        serde_json::to_string(&result).unwrap_or_else(|_| "{}".to_string())
                    }
                });
                
                match result {
                    Ok(json_str) => {
                        match CString::new(json_str) {
                            Ok(c_string) => c_string.into_raw(),
                            Err(_) => std::ptr::null_mut(),
                        }
                    },
                    Err(_) => std::ptr::null_mut(),
                }
            }
        });

        function_registrations.push(quote! {
            (#func_name.to_string(), stringify!(#wrapper_name).to_string())
        });
    }

    // Generate the registration code
    Ok(quote! {
        impl #struct_name {
            #(#function_wrappers)*
            
            /// Get the plugin schema for this plugin
            pub fn get_plugin_schema() -> std::collections::HashMap<String, String> {
                let mut schemas = std::collections::HashMap::new();
                #(schemas.insert #function_schemas;)*
                schemas
            }
            
            /// Register this plugin with the HPD Agent system
            #[cfg(any(test, feature = "internal"))]
            pub fn register_plugin() -> crate::plugins::PluginRegistration {
                crate::plugins::PluginRegistration {
                    name: #plugin_name.to_string(),
                    description: #plugin_description.to_string(),
                    functions: vec![
                        #(#function_registrations),*
                    ],
                    schemas: Self::get_plugin_schema(),
                }
            }
            
            #[cfg(not(any(test, feature = "internal")))]
            pub fn register_plugin() -> hpd_rust_agent::plugins::PluginRegistration {
                hpd_rust_agent::plugins::PluginRegistration {
                    name: #plugin_name.to_string(),
                    description: #plugin_description.to_string(),
                    functions: vec![
                        #(#function_registrations),*
                    ],
                    schemas: Self::get_plugin_schema(),
                }
            }
            
            /// Get all available function names
            pub fn get_function_names() -> Vec<&'static str> {
                vec![
                    #(#function_names),*
                ]
            }
        }
        
        // Manual registration method (called when explicitly requested)
        impl #struct_name {
            /// Manually register this plugin and its executors
            /// This should only be called when the plugin is explicitly added to an agent
            pub fn register_with_agent() {
                // Register plugin metadata
                #[cfg(any(test, feature = "internal"))]
                crate::plugins::register_plugin(Self::register_plugin());
                #[cfg(not(any(test, feature = "internal")))]
                hpd_rust_agent::plugins::register_plugin(Self::register_plugin());
                
                // Register function executors
                #(#executor_registrations)*
            }
        }
    })
}

fn rust_type_to_json_type(rust_type: &str) -> &'static str {
    match rust_type {
        s if s.contains("String") || s.contains("&str") => "string",
        s if s.contains("i32") || s.contains("i64") || s.contains("u32") || s.contains("u64") => "integer",
        s if s.contains("f32") || s.contains("f64") => "number",
        s if s.contains("bool") => "boolean",
        s if s.contains("Vec") || s.contains("Array") => "array",
        _ => "object", // Default for complex types
    }
}
