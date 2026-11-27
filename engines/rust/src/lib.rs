/// Streaming encoder policy. The caller acknowledges only successful encoder changes.
#[derive(Debug)]
pub struct Controller {
    ceiling: u32, current: u32, fps: u32,
    enabled: bool, reduced: bool,
    quiet: u32, active: u32, pressure: u32, cooldown: u32,
    load: u64, output: u64, qp: u64,
}
impl Controller {
    pub fn new(ceiling: u32, fps: u32) -> Self {
        Self { ceiling, current: ceiling, fps: fps.max(1), enabled: false,
            reduced: false, quiet: 0, active: 0, pressure: 0, cooldown: 0,
            load: 1000, output: ceiling.into(), qp: 0 }
    }
    pub fn reset(&mut self, ceiling: u32, fps: u32) {
        let enabled = self.enabled;
        *self = Self::new(ceiling, fps);
        self.enabled = enabled;
    }
    pub fn enable(&mut self, enabled: bool) {
        self.enabled = enabled;
        self.clear_counts();
        self.cooldown = 0;
        self.reduced = false;
    }
    fn clear_counts(&mut self) {
        self.quiet = 0;
        self.active = 0;
        self.pressure = 0;
    }
    pub fn set_ceiling(&mut self, rate: u32) {
        self.ceiling = rate;
        self.applied(rate);
        self.clear_counts();
        self.cooldown = self.fps;
        self.output = rate.into();
    }
    pub fn applied(&mut self, rate: u32) {
        self.current = rate;
        self.reduced = rate < self.ceiling;
    }
    pub fn output_kbps(&self) -> u32 { self.output as u32 }
    pub fn average_qp_x100(&self) -> u32 { self.qp as u32 }
    pub fn observe(&mut self, bytes: u64, qp: u32, idr: bool) -> Option<u32> {
        if !self.enabled || self.ceiling == 0 || self.current == 0 || idr { return None; }
        let fps = u64::from(self.fps);
        let sample = bytes.wrapping_mul(8).wrapping_mul(fps) / 1000;
        self.output = (7 * self.output + sample.min(2 * u64::from(self.ceiling))) / 8;
        let expected = (u64::from(self.current) * 1000 / 8 / fps).max(1);
        let load = bytes.wrapping_mul(1000) / expected;
        self.load = (7 * self.load + load.min(2000)) / 8;
        if qp > 0 {
            let sample = (100 * u64::from(qp)).min(5100);
            self.qp = if self.qp == 0 { sample } else { (7 * self.qp + sample) / 8 };
        }
        if self.cooldown > 0 { self.cooldown -= 1; return None; }
        let pressure = self.qp >= 3600;
        self.pressure = if pressure { self.pressure.wrapping_add(1) } else { 0 };
        self.quiet = if self.load <= 220 && !pressure { self.quiet.wrapping_add(1) } else { 0 };
        self.active = if self.load >= 520 { self.active.wrapping_add(1) } else { 0 };
        if !self.reduced && self.quiet >= self.fps.wrapping_mul(5) {
            self.quiet = 0;
            let floor = (self.ceiling.wrapping_mul(60) / 100).max(4000).min(self.ceiling);
            let demand = (self.output * 125 / 100).min(u64::from(self.ceiling)) as u32;
            let target = demand.max(floor);
            let benefit = (self.ceiling.wrapping_mul(15) / 100).max(1000);
            if self.current > target && self.current - target >= benefit {
                self.cooldown = self.fps.wrapping_mul(2);
                return Some(target);
            }
        }
        if self.reduced && (self.active >= 3 || self.pressure >= 2) {
            self.active = 0;
            self.pressure = 0;
            self.cooldown = self.fps;
            return Some(self.ceiling);
        }
        None
    }
}
