namespace ResourceManager.App.Domain.Messages;

/// <summary>
/// 后端要说的一件事：说哪一类（域）、哪一条（码）、以及这条话需要的事实参数。
/// 措辞和语言由前端按当前语言决定，后端不带任何文案。
/// </summary>
/// <param name="Domain">命名空间，见 <see cref="BackendMessageDomains"/>。</param>
/// <param name="Code">该域内的消息码，见 <see cref="BackendMessageCodes"/>。</param>
/// <param name="Args">渲染这条消息需要的事实（名字、路径、数量的字符串形式），只放事实不放措辞。</param>
public sealed record BackendMessage(byte Domain, byte Code, IReadOnlyList<string> Args)
{
    public static BackendMessage Create(byte domain, byte code, params string[] args)
        => new(domain, code, args ?? []);
}
