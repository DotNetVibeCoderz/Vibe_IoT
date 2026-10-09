//! NTP decoding never panics, packets survive a round trip, and the offset/delay math stays finite.
#![no_main]
use iotcom_ntp::{offset_delay, Packet, Timestamp};
use libfuzzer_sys::fuzz_target;

fuzz_target!(|data: &[u8]| {
    if let Some(p) = Packet::decode(data) {
        assert_eq!(p.encode(), data);
        let _ = p.reference_text();
        let _ = p.is_kiss_of_death();
        let (o, d) = offset_delay(p.reference, p.originate, p.receive, p.transmit);
        assert!(o.is_finite() && d.is_finite() && d >= 0.0);
        let _ = p.transmit.ticks();
        let _ = Timestamp::difference(p.receive, p.originate);
    }
});
