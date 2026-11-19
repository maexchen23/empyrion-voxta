using VoxtaBridge;
using Xunit;

namespace VoxtaBridge.Mod.Tests;

public class FormTests
{
    [Fact]
    public void Round_trip_with_special_characters()
    {
        var text = Form.Encode(("item", "45mm Ammo"), ("to", "Hold @ Hauler"), ("note", "a&b=c+d ü"), ("skip", null));
        var d = Form.Parse(text);
        Assert.Equal("45mm Ammo", d["item"]);
        Assert.Equal("Hold @ Hauler", d["to"]);
        Assert.Equal("a&b=c+d ü", d["note"]);
        Assert.False(d.ContainsKey("skip"));
    }

    [Fact]
    public void Plus_is_space_and_get_trims()
    {
        var d = Form.Parse("item=combat+knife&amount=+5+&empty=");
        Assert.Equal("combat knife", d.Get("item"));
        Assert.Equal("5", d.Get("amount"));
        Assert.Null(d.Get("empty"));
        Assert.Null(d.Get("missing"));
    }
}
