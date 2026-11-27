use piulink_stream_rate::Controller;
use std::io::{self, BufRead};
fn main() -> Result<(), Box<dyn std::error::Error>> {
    let mut rate = Controller::new(0, 60);
    for line in io::stdin().lock().lines() {
        let line = line?;
        let fields: Vec<_> = line.split_whitespace().collect();
        let mut target = None;
        match fields.as_slice() {
            ["reset", kbps, fps] => rate.reset(kbps.parse()?, fps.parse()?),
            ["enable", flag] => rate.enable(*flag == "1"),
            ["ceiling", kbps] => rate.set_ceiling(kbps.parse()?),
            ["applied", kbps] => rate.applied(kbps.parse()?),
            ["frame", bytes, qp, key] => target = rate.observe(bytes.parse()?, qp.parse()?, *key == "1"),
            _ => return Err("invalid trace event".into()),
        }
        println!("{} {} {}", target.unwrap_or(0), rate.output_kbps(), rate.average_qp_x100());
    }
    Ok(())
}
