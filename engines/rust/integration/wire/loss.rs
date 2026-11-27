/// 0 waits for more shards, 1 runs frame completion, 2 reports a predicted loss.
#[no_mangle]
pub extern "C" fn pl_block_readiness(total: u32, needed: u32, pending: u32,
                                      missing: u32, reported: bool, reordered: bool) -> u8 {
    if needed > total { return 0; }
    if pending >= needed { return 1; }
    if !reported && !reordered && missing > total - needed { return 2; }
    0
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn predicts_once_and_allows_late_recovery() {
        assert_eq!(pl_block_readiness(6, 4, 3, 2, false, false), 0);
        assert_eq!(pl_block_readiness(6, 4, 3, 3, false, false), 2);
        assert_eq!(pl_block_readiness(6, 4, 3, 3, true, false), 0);
        assert_eq!(pl_block_readiness(6, 4, 3, 3, false, true), 0);
        assert_eq!(pl_block_readiness(6, 4, 4, 3, true, true), 1);
    }
}
