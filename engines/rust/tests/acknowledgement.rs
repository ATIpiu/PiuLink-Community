use piulink_stream_rate::Controller;

#[test]
fn failed_encoder_change_retries_without_entering_reduced_state() {
    let mut rate = Controller::new(20_000, 60);
    rate.enable(true);
    let request = (0..420).find_map(|_| rate.observe(400, 24, false));
    assert_eq!(request, Some(12_000));
    // Encoder declined. Feed more quiet frames without acknowledging the request.
    let retry = (0..600).find_map(|_| rate.observe(400, 24, false));
    assert_eq!(retry, request);
    rate.applied(retry.unwrap());
    let restored = (0..200).find_map(|_| rate.observe(400, 42, false));
    assert_eq!(restored, Some(20_000));
}
