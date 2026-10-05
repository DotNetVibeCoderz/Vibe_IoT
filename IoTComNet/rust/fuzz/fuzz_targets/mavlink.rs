//! MAVLink parser on adversarial bytes in random chunking: never panics, every frame it returns re-encodes to a
//! frame that parses to the same content (unsigned frames).
#![no_main]
use iotcom_mavlink::{encode, MessageInfo, Parser};
use libfuzzer_sys::fuzz_target;

fn lookup(id: u32) -> Option<MessageInfo> {
    // A synthetic dialect: every id below 512 is known with a derived CRC_EXTRA and length.
    (id < 512).then(|| MessageInfo { crc_extra: (id as u8).wrapping_mul(31), max_len: (id % 64) as u8 + 1 })
}

fuzz_target!(|data: &[u8]| {
    let Some((&chunk, rest)) = data.split_first() else { return };
    let chunk = usize::from(chunk % 32) + 1;
    let mut p = Parser::new(lookup);
    for piece in rest.chunks(chunk) {
        p.feed(piece);
        while let Some(f) = p.next_frame() {
            if f.signature.is_some() || f.payload.len() > 255 {
                continue;
            }
            let mut out = Vec::new();
            encode(&f.header(), &f.payload, lookup(f.msg_id).unwrap().crc_extra, &mut out);
            let mut q = Parser::new(lookup);
            q.feed(&out);
            let g = q.next_frame().expect("re-encoded frame parses");
            assert_eq!((g.msg_id, g.seq, g.sys_id, g.comp_id), (f.msg_id, f.seq, f.sys_id, f.comp_id));
        }
    }
});
