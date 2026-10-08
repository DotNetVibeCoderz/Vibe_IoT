//! Diagnostic: print the USB and HID device lists through the C ABI. `cargo run -p iotcom-usb-native --example list`

fn main() {
    let mut buf = vec![0u8; 1 << 20];
    let mut written = 0usize;
    // SAFETY: buffers are valid for their lengths.
    unsafe {
        let s = iotcom_usb::iotcom_usb_list(buf.as_mut_ptr(), buf.len(), &mut written);
        println!("usb ({s}): {}", String::from_utf8_lossy(&buf[..written]));
        let s = iotcom_usb::iotcom_hid_list(buf.as_mut_ptr(), buf.len(), &mut written);
        println!("hid ({s}): {}", String::from_utf8_lossy(&buf[..written.min(600)]));
    }
}
