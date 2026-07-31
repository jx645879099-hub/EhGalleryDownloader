# 隐私与数据说明

## Cookie 权限

浏览器导入组件只拥有以下范围：

- `e-hentai.org`
- `exhentai.org`
- 本机回环地址，用于把用户主动授权的结果交给桌面软件

它只传递登录所需的 `ipb_member_id`、`ipb_pass_hash` 和 `igneous`，不会读取浏览器密码库、历史记录或其他网站 Cookie，也不会把内容上传到第三方服务器。

## 本机保存

- 手动填写或网页导入的 Cookie 只有验证成功后才会替换已有副本。
- Cookie 使用 Windows DPAPI 按当前 Windows 用户加密，其他 Windows 账户无法直接解密。
- Cookie 不写入下载日志、任务文件或 GitHub 仓库。
- 点击软件中的“清除”会删除输入内容和本机加密副本。

Cookie 等同临时登录密码，请勿发送到聊天、截图、问题报告或云端文档。

## 运行文件

软件会在 `%LOCALAPPDATA%\EhGalleryDownloader` 保存设置、任务队列、运行日志和临时运行配置。包含 Cookie 的临时配置会在任务结束后删除；启动时也会清理异常退出遗留的临时配置。

仓库的 `.gitignore` 排除了编译结果、Cookie、本地设置、日志、下载内核、快捷方式和验收下载内容。
