import { bundleManager, common } from '@kit.AbilityKit';
import { fileIo as fs, picker } from '@kit.CoreFileKit';
import { checkRelease, HapRelease, HapResult, prepareRelease, mib } from '../service/ReleaseUpdate';

@Component
struct UpdatePage {
  @State private installed: string = '';
  @State private offer?: HapRelease = undefined;
  @State private verified?: HapResult = undefined;
  @State private busy: boolean = false;
  @State private status: string = '';
  private ctx(): common.UIAbilityContext { return this.getUIContext().getHostContext() as common.UIAbilityContext; }

  aboutToAppear(): void {
    this.installed = bundleManager.getBundleInfoForSelfSync(bundleManager.BundleFlag.GET_BUNDLE_INFO_DEFAULT).versionName;
    this.check();
  }

  private async check(): Promise<void> {
    if (this.busy) return;
    this.busy = true;
    this.status = '正在检查 GitHub 更新';
    try {
      this.offer = await checkRelease(this.installed);
      this.status = this.offer ? '发现新版本 ' + this.offer.version : '当前已是最新版本';
    } catch (e) { this.status = '检查失败，' + (e as Error).message; }
    finally { this.busy = false; }
  }

  private async download(selectOld: boolean): Promise<void> {
    const r = this.offer;
    if (this.busy || !r) return;
    this.busy = true;
    try {
      let old = '';
      if (selectOld) {
        const files = await new picker.DocumentViewPicker(this.ctx()).select({ maxSelectNumber: 1 });
        if (!files.length) return;
        old = files[0];
      }
      this.status = '正在检查可复用的 HAP 内容';
      this.verified = await prepareRelease(r, this.ctx().cacheDir, this.installed, this.ctx().bundleCodeDir,
        old, (message: string) => { this.status = message; });
      this.status = '已通过校验。下载 ' + mib(this.verified.downloaded) + ' MiB，复用 ' + mib(this.verified.reused) + ' MiB';
    } catch (e) { this.status = '未完成下载，' + (e as Error).message; }
    finally { this.busy = false; }
  }

  private async save(): Promise<void> {
    const result = this.verified;
    const release = this.offer;
    if (!result || !release || this.busy) return;
    this.busy = true;
    try {
      const paths = await new picker.DocumentViewPicker(this.ctx()).save({ newFileNames: [release.name] });
      if (!paths.length) return;
      const target = fs.openSync(paths[0], fs.OpenMode.WRITE_ONLY | fs.OpenMode.TRUNC);
      try { await fs.copyFile(result.path, target.fd); }
      finally { fs.closeSync(target); }
      this.status = 'HAP 已保存。请使用原签名工具覆盖安装以保留配对。';
    } catch (e) { this.status = '保存未完成，可再次选择保存位置。'; }
    finally { this.busy = false; }
  }

  build() {
    NavDestination() {
      Scroll() {
        Column({ space: 16 }) {
          Text('PiuLink ' + this.installed).fontSize(20).fontWeight(FontWeight.Medium)
          Text(this.status).fontSize(16)
          if (this.busy) { LoadingProgress().width(28).height(28) }
          if (this.verified) {
            Button('保存 HAP').enabled(!this.busy).onClick(() => { this.save(); })
          } else if (this.offer) {
            Text('完整包 ' + mib(this.offer.bytes) + ' MiB。可复用已安装的 HAP、下载缓存或手动选择的旧包。')
              .fontSize(14).opacity(0.72)
            Button('下载更新').enabled(!this.busy).onClick(() => { this.download(false); })
            Button('选择旧 HAP').enabled(!this.busy).onClick(() => { this.download(true); })
          } else if (!this.busy) {
            Button('重新检查').onClick(() => { this.check(); })
          }
        }.alignItems(HorizontalAlign.Start).padding(24).width('100%')
      }.align(Alignment.TopStart).width('100%').height('100%')
    }.title('检查更新')
  }
}

@Builder
export function UpdatePageBuilder() { UpdatePage() }
