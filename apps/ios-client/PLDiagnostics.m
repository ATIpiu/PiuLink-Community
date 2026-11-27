#import "PLDiagnostics.h"
@import MetricKit;

static NSString *const EventsKey = @"PiuLinkDiagnosticEventsV1";

@interface PLReporter : NSObject <MXMetricManagerSubscriber, NSURLSessionTaskDelegate>
@end

@implementation PLReporter
- (void)URLSession:(NSURLSession *)session task:(NSURLSessionTask *)task
        willPerformHTTPRedirection:(NSHTTPURLResponse *)response newRequest:(NSURLRequest *)request
        completionHandler:(void (^)(NSURLRequest *))completionHandler {
    completionHandler(nil);
}

static long long nowSeconds(void) {
    return (long long)NSDate.date.timeIntervalSince1970;
}
static void appendEvents(NSArray *events) {
    @synchronized (PLReporter.class) {
        NSUserDefaults *store = NSUserDefaults.standardUserDefaults;
        NSMutableArray *saved = [([store arrayForKey:EventsKey] ?: @[]) mutableCopy];
        [saved addObjectsFromArray:events];
        if (saved.count > 64) [saved removeObjectsInRange:NSMakeRange(0, saved.count - 64)];
        [store setObject:saved forKey:EventsKey];
    }
}

- (void)didReceiveDiagnosticPayloads:(NSArray<MXDiagnosticPayload *> *)payloads {
    for (MXDiagnosticPayload *payload in payloads) {
        NSNumber *time = @((long long)payload.timeStampEnd.timeIntervalSince1970);
        for (MXCrashDiagnostic *crash in payload.crashDiagnostics) {
            NSMutableArray *rows = [NSMutableArray arrayWithObject:@{
                @"time":time, @"code":@"crash.signal", @"value":@(MAX(0, MIN(255, crash.signal.intValue))) }];
            NSData *data = crash.callStackTree.JSONRepresentation;
            if (data && data.length <= 2 * 1024 * 1024) {
                NSDictionary *tree = [NSJSONSerialization JSONObjectWithData:data options:0 error:nil];
                NSString *executable = [NSBundle.mainBundle objectForInfoDictionaryKey:@"CFBundleExecutable"];
                for (NSDictionary *stack in tree[@"callStacks"]) {
                    if (![stack[@"threadAttributed"] boolValue]) continue;
                    NSMutableArray *pending = [NSMutableArray arrayWithArray:stack[@"callStackRootFrames"] ?: @[]];
                    for (int visited = 0; pending.count && visited < 256 && rows.count < 17; visited++) {
                        NSDictionary *frame = pending.lastObject;
                        [pending removeLastObject];
                        NSNumber *offset = frame[@"offsetIntoBinaryTextSegment"];
                        if ([frame[@"binaryName"] isEqual:executable] && [offset isKindOfClass:NSNumber.class]
                                && offset.longLongValue >= 0 && offset.longLongValue <= INT32_MAX) {
                            [rows addObject:@{@"time":time, @"code":@"crash.native_frame",
                                              @"symbol":@"ios_app", @"value":offset}];
                        }
                        [pending addObjectsFromArray:frame[@"subFrames"] ?: @[]];
                    }
                }
            }
            appendEvents(rows);
        }
    }
}
@end

void PLConnectionFailed(NSString *stage, int errorCode) {
    appendEvents(@[@{@"time":@(nowSeconds()), @"code":[@"connect." stringByAppendingString:stage],
        @"reason":@"unknown", @"value":@(errorCode)}]);
}

static PLReporter *reporter;
void PLStartDiagnostics(void) {
    reporter = [PLReporter new];
    [MXMetricManager.sharedManager addSubscriber:reporter];
    appendEvents(@[@{@"time":@(nowSeconds()), @"code":@"app.start"}]);
}

void PLConfirmDiagnosticUpload(UIViewController *owner) {
    UIAlertController *confirm = [UIAlertController alertControllerWithTitle:@"上传诊断日志？"
        message:@"发送应用版本、连接失败阶段和系统提供的崩溃位置。单次最多 16 KiB。" preferredStyle:UIAlertControllerStyleAlert];
    [confirm addAction:[UIAlertAction actionWithTitle:@"取消" style:UIAlertActionStyleCancel handler:nil]];
    [confirm addAction:[UIAlertAction actionWithTitle:@"上传" style:UIAlertActionStyleDefault handler:^(UIAlertAction *action) {
        NSArray *events = [NSUserDefaults.standardUserDefaults arrayForKey:EventsKey] ?: @[];
        NSString *version = [NSBundle.mainBundle objectForInfoDictionaryKey:@"CFBundleShortVersionString"];
        NSDictionary *payload = @{@"schema":@1, @"platform":@"ios", @"version":version, @"events":events};
        NSMutableURLRequest *req = [NSMutableURLRequest requestWithURL:
            [NSURL URLWithString:@"https://atipiu.org:8443/piulink/v1/diagnostics"]];
        req.HTTPMethod = @"POST";
        req.HTTPBody = [NSJSONSerialization dataWithJSONObject:payload options:0 error:nil];
        if (req.HTTPBody.length > 16384) return;
        [req setValue:@"application/json" forHTTPHeaderField:@"Content-Type"];
        NSURLSessionConfiguration *cfg = NSURLSessionConfiguration.ephemeralSessionConfiguration;
        cfg.HTTPShouldSetCookies = NO;
        cfg.URLCredentialStorage = nil;
        cfg.timeoutIntervalForResource = 18;
        NSURLSession *session = [NSURLSession sessionWithConfiguration:cfg delegate:reporter delegateQueue:nil];
        [[session dataTaskWithRequest:req completionHandler:^(NSData *data, NSURLResponse *response, NSError *error) {
            NSString *idText = data.length == 20 ? [[NSString alloc] initWithData:data encoding:NSASCIIStringEncoding] : nil;
            BOOL accepted = !error && [(NSHTTPURLResponse *)response statusCode] == 201 &&
                idText.length == 20 && [idText rangeOfString:@"^[a-f0-9]{20}$" options:NSRegularExpressionSearch].location != NSNotFound;
            dispatch_async(dispatch_get_main_queue(), ^{
                UIAlertController *result = [UIAlertController alertControllerWithTitle:accepted ? @"上传成功" : @"上传失败"
                    message:accepted ? [@"报告编号  " stringByAppendingString:idText] : @"请稍后重试，本机记录已保留。"
                    preferredStyle:UIAlertControllerStyleAlert];
                [result addAction:[UIAlertAction actionWithTitle:@"好" style:UIAlertActionStyleDefault handler:nil]];
                if (owner.view.window) [owner presentViewController:result animated:YES completion:nil];
            });
        }] resume];
        [session finishTasksAndInvalidate];
    }]];
    [owner presentViewController:confirm animated:YES completion:nil];
}
