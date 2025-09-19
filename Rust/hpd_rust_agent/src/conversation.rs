use crate::{ffi, agent::Agent};
use std::{mem, ffi::{c_void, CStr, CString}};
use tokio_stream::{Stream, wrappers::UnboundedReceiverStream};

pub struct Conversation {
    handle: *mut c_void,
}

impl Conversation {
    /// Create a Conversation from an existing handle (for internal use)
    pub(crate) fn from_handle(handle: *mut c_void) -> Self {
        Self { handle }
    }

    pub fn new(agents: Vec<Agent>) -> Result<Self, String> {
        if agents.is_empty() {
            return Err("At least one agent is required to create a conversation".to_string());
        }
        
        let agent_handles: Vec<*mut c_void> = agents.iter().map(|a| a.handle).collect();
        
        let conversation_handle = unsafe {
            ffi::create_conversation(agent_handles.as_ptr(), agent_handles.len() as i32)
        };
        
        // Prevent Rust from dropping the agents now that C# holds a reference
        mem::forget(agents);

        if conversation_handle.is_null() {
            Err("Failed to create conversation on C# side.".to_string())
        } else {
            Ok(Self { handle: conversation_handle })
        }
    }

    pub fn send(&self, message: &str) -> Result<String, String> {
        let c_message = CString::new(message).map_err(|_| "Message contains null bytes".to_string())?;

        let response_ptr = unsafe {
            ffi::conversation_send(self.handle, c_message.as_ptr())
        };

        if response_ptr.is_null() {
            return Err("Failed to get response from agent.".to_string());
        }

        let c_str = unsafe { CStr::from_ptr(response_ptr) };
        let response = c_str.to_str().map_err(|_| "Response contains invalid UTF-8".to_string())?.to_owned();

        // Free the string allocated by C#
        unsafe { ffi::free_string(response_ptr as *mut c_void) };

        Ok(response)
    }

    pub fn send_streaming(
        &self,
        message: &str,
    ) -> Result<impl Stream<Item = String>, String> {
        let (context_key, rx) = crate::streaming::create_stream();
        let c_message = CString::new(message).map_err(|_| "Message contains null bytes".to_string())?;

        unsafe {
            ffi::conversation_send_streaming(
                self.handle,
                c_message.as_ptr(),
                crate::streaming::stream_callback as *const _,
                context_key as *mut c_void,
            );
        }

        Ok(UnboundedReceiverStream::new(rx))
    }

    pub fn send_simple(
        &self,
        message: &str,
    ) -> Result<impl Stream<Item = String>, String> {
        let (context_key, rx) = crate::streaming::create_stream();
        let c_message = CString::new(message).map_err(|_| "Message contains null bytes".to_string())?;

        unsafe {
            ffi::conversation_send_simple(
                self.handle,
                c_message.as_ptr(),
                crate::streaming::stream_callback as *const _,
                context_key as *mut c_void,
            );
        }

        Ok(UnboundedReceiverStream::new(rx))
    }
}

impl Drop for Conversation {
    fn drop(&mut self) {
        if !self.handle.is_null() {
            unsafe { ffi::destroy_conversation(self.handle) };
            self.handle = std::ptr::null_mut();
        }
    }
}

// Send and Sync are safe because the C# side manages thread safety
unsafe impl Send for Conversation {}
unsafe impl Sync for Conversation {}
