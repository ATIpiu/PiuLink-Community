// The HTTPS handler holds the display lease mutex throughout this transaction.
use std::ffi::c_void;

#[repr(C)]
pub struct LaunchFacts {
    resume: bool,
    keys: bool,
    audio: bool,
    app: bool,
    running: i32,
    idle: bool,
    prepared: bool,
    lease_conflict: bool,
}

#[repr(C)]
#[derive(Debug, PartialEq)]
pub struct LaunchResult {
    reason: i32,
    status: i32,
    rollback: bool,
}

#[repr(i32)]
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum Action {
    KeepAudio,
    SetAudio,
    BindCapture,
    ProbeDisplay,
    EncryptionAllowed,
    Execute,
    Publish,
}

fn run(f: &LaunchFacts, mut perform: impl FnMut(Action) -> i32) -> LaunchResult {
    let mut rollback = false;
    let result = |reason, status, rollback| LaunchResult {
        reason,
        status,
        rollback,
    };
    if f.resume && f.running == 0 {
        return result(1, 503, false);
    }
    if !f.keys || (!f.resume && !(f.audio && f.app)) {
        return result(2, 400, false);
    }
    if !f.resume && f.running > 0 {
        return result(3, 400, false);
    }
    if !f.prepared && f.lease_conflict {
        return result(4, 409, false);
    }

    perform(if f.audio && (!f.resume || f.idle) {
        Action::SetAudio
    } else {
        Action::KeepAudio
    });
    if f.prepared {
        perform(Action::BindCapture);
    } else if f.idle {
        rollback = !f.resume;
        if perform(Action::ProbeDisplay) != 0 {
            return result(5, 503, rollback);
        }
    }
    if perform(Action::EncryptionAllowed) == 0 {
        return result(6, 403, rollback);
    }
    if !f.resume {
        let status = perform(Action::Execute);
        if status != 0 {
            return result(7, status, rollback);
        }
    }
    perform(Action::Publish);
    result(0, 200, false)
}

#[no_mangle]
pub unsafe extern "C" fn pl_https_launch(
    f: &LaunchFacts,
    ctx: *mut c_void,
    perform: unsafe extern "C" fn(*mut c_void, Action) -> i32,
) -> LaunchResult {
    run(f, |action| perform(ctx, action))
}

#[cfg(test)]
mod tests {
    use super::*;
    fn facts() -> LaunchFacts {
        LaunchFacts {
            resume: false,
            keys: true,
            audio: true,
            app: true,
            running: 0,
            idle: true,
            prepared: false,
            lease_conflict: false,
        }
    }
    #[test]
    fn lease_rejected_before_side_effects() {
        let f = LaunchFacts {
            lease_conflict: true,
            ..facts()
        };
        assert_eq!(run(&f, |_| panic!()).status, 409);
    }
    #[test]
    fn failed_encryption_rolls_back_new_display() {
        let mut actions = Vec::new();
        let r = run(&facts(), |a| {
            actions.push(a);
            0
        });
        assert_eq!(
            r,
            LaunchResult {
                reason: 6,
                status: 403,
                rollback: true
            }
        );
        assert_eq!(
            actions,
            [
                Action::SetAudio,
                Action::ProbeDisplay,
                Action::EncryptionAllowed
            ]
        );
    }
    #[test]
    fn prepared_resume_preserves_audio_and_existing_app() {
        let f = LaunchFacts {
            resume: true,
            running: 1,
            idle: false,
            prepared: true,
            ..facts()
        };
        let mut actions = Vec::new();
        assert_eq!(
            run(&f, |a| {
                actions.push(a);
                1
            })
            .status,
            200
        );
        assert_eq!(
            actions,
            [
                Action::KeepAudio,
                Action::BindCapture,
                Action::EncryptionAllowed,
                Action::Publish
            ]
        );
    }
}
