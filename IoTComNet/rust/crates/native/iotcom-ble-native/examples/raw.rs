//! Diagnostic: plain btleplug scan, no IoTCom code. `cargo run -p iotcom-ble-native --example raw`
use btleplug::api::{Central, Manager as _, Peripheral as _, ScanFilter};
use btleplug::platform::Manager;

#[tokio::main]
async fn main() -> Result<(), Box<dyn std::error::Error>> {
    let manager = Manager::new().await?;
    let adapter = manager.adapters().await?.into_iter().next().ok_or("no adapter")?;
    println!("adapter: {}", adapter.adapter_info().await?);
    adapter.start_scan(ScanFilter::default()).await?;
    tokio::time::sleep(std::time::Duration::from_secs(10)).await;
    let peripherals = adapter.peripherals().await?;
    println!("{} peripherals", peripherals.len());
    for p in peripherals.iter().take(10) {
        let props = p.properties().await?;
        println!("{} {:?} {:?}", p.id(), props.as_ref().and_then(|x| x.local_name.clone()), props.and_then(|x| x.rssi));
    }
    Ok(())
}
