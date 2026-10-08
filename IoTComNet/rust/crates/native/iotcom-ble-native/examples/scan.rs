//! Diagnostic: scan through the C ABI for a few seconds and print every queued event.
//! `cargo run -p iotcom-ble-native --example scan -- 10`

fn main() {
    let seconds: u64 = std::env::args().nth(1).and_then(|s| s.parse().ok()).unwrap_or(10);
    let mut handle = std::ptr::null_mut();
    // SAFETY: valid out pointer; the handle is used on this thread only and freed once.
    unsafe {
        let status = iotcom_ble::iotcom_ble_new(&mut handle);
        if status != 0 {
            let mut buf = [0u8; 256];
            let n = iotcom_ble::iotcom_last_error(buf.as_mut_ptr(), buf.len()) as usize;
            eprintln!("open failed ({status}): {}", String::from_utf8_lossy(&buf[..n.min(buf.len())]));
            return;
        }
        println!("scan start: {}", iotcom_ble::iotcom_ble_start_scan(handle, std::ptr::null(), 0));
        let mut buf = vec![0u8; 8192];
        let mut events = 0;
        let end = std::time::Instant::now() + std::time::Duration::from_secs(seconds);
        while std::time::Instant::now() < end {
            let mut written = 0usize;
            if iotcom_ble::iotcom_ble_poll_event(handle, buf.as_mut_ptr(), buf.len(), &mut written) == 1 {
                events += 1;
                if events <= 20 {
                    println!("{}", String::from_utf8_lossy(&buf[..written]));
                }
            } else {
                std::thread::sleep(std::time::Duration::from_millis(20));
            }
        }
        println!("{events} events");
        iotcom_ble::iotcom_ble_stop_scan(handle);
        iotcom_ble::iotcom_ble_free(handle);
    }
}
