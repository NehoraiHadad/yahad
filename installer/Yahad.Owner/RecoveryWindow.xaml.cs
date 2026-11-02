using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Yahad.Authority;
namespace Yahad.Owner;

public partial class RecoveryWindow:Window
{
    private readonly string installation;private readonly Func<AuthorityFrame,CancellationToken,Task<EndpointReply>> send;
    private readonly CancellationTokenSource closed=new();private readonly DispatcherTimer refresh=new(){Interval=TimeSpan.FromSeconds(10)};
    private bool busy;
    internal RecoveryWindow(string installation,Func<AuthorityFrame,CancellationToken,Task<EndpointReply>> send)
    {
        InitializeComponent();this.installation=installation;this.send=send;
        Loaded+=async(_,_)=>{await RefreshAsync();refresh.Start();};refresh.Tick+=async(_,_)=>await RefreshAsync();
        Closing+=(_,_)=>{refresh.Stop();closed.Cancel();};
    }
    private AuthorityFrame Frame(string method,object body)=>new(1,Guid.NewGuid().ToString("N"),method,JsonSerializer.SerializeToElement(body));
    private async void Refresh_Click(object sender,RoutedEventArgs e)=>await RefreshAsync();
    private async Task RefreshAsync()
    {
        if(busy||closed.IsCancellationRequested)return;busy=true;Refresh.IsEnabled=false;Prepare.IsEnabled=false;
        try
        {
            var previous=(Operations.SelectedItem as Choice)?.Item.OperationId;var frame=Frame("recovery",new{});var reply=await send(frame,closed.Token);
            if(reply.Id!=frame.Id||reply.LastRequest is not { } body)throw new IOException("OWNER_RECOVERY_STATUS_BINDING");
            var summary=body.Deserialize<OwnerRecoverySummary>()??throw new IOException("OWNER_RECOVERY_STATUS_MISSING");
            var choices=summary.Operations.Select((item,index)=>new Choice(item,$"עדכון שהופסק {index+1}")).ToArray();
            Operations.ItemsSource=choices;Operations.SelectedItem=choices.FirstOrDefault(x=>x.Item.OperationId==previous)??choices.FirstOrDefault();
            Status.Text=summary.State switch
            {
                "healthy"=>"ההתאוששות הושלמה ונבדק שהיישום פועל. אפשר לפתוח שוב את יחד.",
                "recovering"=>"ההתאוששות מתבצעת. ניתן להשאיר את החלון פתוח ולבדוק כאן את המצב.",
                "awaiting_local_approval"=>"ממתינה בקשת אישור. ניתן להציג אותה שוב באמצעות הכפתור.",
                _=>summary.Error is not null?"לא ניתן להשלים כרגע את בדיקת ההתאוששות. הראיות והעותקים נשמרו; אפשר לבדוק שוב את המצב.":choices.Length==0?"לא נמצאו עדכונים שהופסקו הדורשים התאוששות.":"נמצא עדכון שהופסק. אפשר להציג אפשרות התאוששות ולקרוא את פרטי האישור לפני שמחליטים."
            };
            Prepare.IsEnabled=summary.State is not("recovering" or "healthy")&&(choices.Length>0||reply.Confirmation is not null);
        }
        catch(OperationCanceledException)when(closed.IsCancellationRequested){}
        catch{Status.Text="לא ניתן לקרוא כרגע את המצב. לא נשלחה בקשת שחזור; אפשר לבחור בדיקת מצב שוב.";}
        finally{busy=false;if(!closed.IsCancellationRequested)Refresh.IsEnabled=true;}
    }
    private async void Prepare_Click(object sender,RoutedEventArgs e)
    {
        if(busy||Operations.SelectedItem is not Choice choice)return;busy=true;Prepare.IsEnabled=false;Refresh.IsEnabled=false;
        try
        {
            var request=Frame("recover",new{choice.Item.OperationId});var reply=await send(request,closed.Token);
            if(reply.Id!=request.Id||reply.Confirmation is not { } confirmation)throw new IOException("OWNER_RECOVERY_CONFIRMATION_MISSING");
            var state=new OwnerConfirmationState(confirmation,installation,()=>DateTimeOffset.UtcNow);
            var window=new ApprovalWindow(state,(answer,token)=>
            {
                if(answer.Method!="answer")throw new IOException("OWNER_RECOVERY_ANSWER_METHOD");
                return send(answer with{Method="recovery-answer"},token);
            }){Owner=this};
            window.ShowDialog();
        }
        catch(OperationCanceledException)when(closed.IsCancellationRequested){}
        catch{Status.Text="לא ניתן להציג כרגע אפשרות בטוחה. ייתכן שבקשת ההכנה נקלטה; יש לבדוק מצב לפני ניסיון נוסף. לא נשלח אישור שחזור.";}
        finally{busy=false;if(!closed.IsCancellationRequested){Refresh.IsEnabled=true;await RefreshAsync();}}
    }
    private sealed record Choice(OwnerRecoveryItem Item,string Label);
}
