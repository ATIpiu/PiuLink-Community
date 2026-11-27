#import "PLUpdate.h"
#import <Mobile/Mobile.h>
#import <UniformTypeIdentifiers/UniformTypeIdentifiers.h>
#import <objc/runtime.h>

@interface PLReleaseUpdate : NSObject <UIDocumentPickerDelegate>
@property(nonatomic, weak) UIViewController *owner;
@property(nonatomic, copy) NSString *version;
@end

@implementation PLReleaseUpdate
- (void)message:(NSString *)text {
    if (!_owner.view.window) return;
    UIAlertController *a = [UIAlertController alertControllerWithTitle:@"软件更新" message:text preferredStyle:UIAlertControllerStyleAlert];
    [a addAction:[UIAlertAction actionWithTitle:@"好" style:UIAlertActionStyleDefault handler:nil]];
    [_owner presentViewController:a animated:YES completion:nil];
}
- (void)prepare:(NSURL *)seed {
    NSURL *cache = [[[NSFileManager defaultManager] URLsForDirectory:NSCachesDirectory inDomains:NSUserDomainMask].firstObject URLByAppendingPathComponent:@"PiuLinkUpdates"];
    dispatch_async(dispatch_get_global_queue(QOS_CLASS_UTILITY, 0), ^{
        BOOL scoped = [seed startAccessingSecurityScopedResource];
        NSError *error = nil;
        NSString *json = MobilePrepareUpdate(@"ios-client", self.version, cache.path, seed.path ?: @"", &error);
        if (scoped) [seed stopAccessingSecurityScopedResource];
        NSDictionary *result = json ? [NSJSONSerialization JSONObjectWithData:[json dataUsingEncoding:NSUTF8StringEncoding] options:0 error:&error] : nil;
        dispatch_async(dispatch_get_main_queue(), ^{
            if (!result[@"path"]) { [self message:error.localizedDescription ?: @"无法准备 IPA。"]; return; }
            UIViewController *owner = self.owner;
            if (!owner.view.window) return;
            NSURL *file = [NSURL fileURLWithPath:result[@"path"]];
            UIActivityViewController *share = [[UIActivityViewController alloc] initWithActivityItems:@[file] applicationActivities:nil];
            share.popoverPresentationController.sourceView = owner.view;
            share.popoverPresentationController.sourceRect = CGRectMake(CGRectGetMidX(owner.view.bounds), CGRectGetMidY(owner.view.bounds), 1, 1);
            [owner presentViewController:share animated:YES completion:nil];
        });
    });
}
- (void)documentPicker:(UIDocumentPickerViewController *)controller didPickDocumentsAtURLs:(NSArray<NSURL *> *)urls {
    if (urls.firstObject) [self prepare:urls.firstObject];
}
- (void)check {
    dispatch_async(dispatch_get_global_queue(QOS_CLASS_UTILITY, 0), ^{
        NSError *error = nil;
        NSString *json = MobileCheckUpdate(@"ios-client",self.version,&error);
        NSDictionary *release = json && ![json isEqualToString:@"null"] ? [NSJSONSerialization JSONObjectWithData:[json dataUsingEncoding:NSUTF8StringEncoding] options:0 error:&error] : nil;
        dispatch_async(dispatch_get_main_queue(), ^{
            if (!release) { [self message:error.localizedDescription ?: @"当前已是最新版本。"]; return; }
            UIAlertController *a = [UIAlertController alertControllerWithTitle:[NSString stringWithFormat:@"更新到 %@",release[@"version"]] message:@"选择以前下载的官方 IPA 可以节省流量。校验完成后，用现有签名工具安装。" preferredStyle:UIAlertControllerStyleAlert];
            [a addAction:[UIAlertAction actionWithTitle:@"选择旧 IPA" style:UIAlertActionStyleDefault handler:^(UIAlertAction *action) {
                UIDocumentPickerViewController *picker = [[UIDocumentPickerViewController alloc] initForOpeningContentTypes:@[UTTypeData] asCopy:NO];
                picker.delegate = self;
                [self.owner presentViewController:picker animated:YES completion:nil];
            }]];
            [a addAction:[UIAlertAction actionWithTitle:@"使用本机缓存下载" style:UIAlertActionStyleDefault handler:^(UIAlertAction *action) { [self prepare:nil]; }]];
            [a addAction:[UIAlertAction actionWithTitle:@"取消" style:UIAlertActionStyleCancel handler:nil]];
            if (self.owner.view.window) [self.owner presentViewController:a animated:YES completion:nil];
        });
    });
}
@end

void PLCheckUpdate(UIViewController *owner) {
    static char key;
    PLReleaseUpdate *update = [[PLReleaseUpdate alloc] init];
    update.owner = owner;
    update.version = [NSBundle.mainBundle objectForInfoDictionaryKey:@"CFBundleShortVersionString"];
    objc_setAssociatedObject(owner, &key, update, OBJC_ASSOCIATION_RETAIN_NONATOMIC);
    [update check];
}
