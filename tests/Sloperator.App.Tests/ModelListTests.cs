using Sloperator.App.ViewModels;
using Xunit;

namespace Sloperator.App.Tests;

/// <summary>The model dropdown's list: sorted by name, whatever order the server lists them in.</summary>
public sealed class ModelListTests
{
    [Fact]
    public void Models_go_in_by_name_ignoring_case_and_numbers_by_value()
    {
        var models = new List<string>();
        foreach (var model in new[] { "qwen3-14b", "Gemma-4-31b", "openai/gpt-oss-120b", "qwen3-8b", "gemma-4-12b", "Qwen3.8-27B" })
            MainWindowViewModel.InsertModel(models, model);
        Assert.Equal(["gemma-4-12b", "Gemma-4-31b", "openai/gpt-oss-120b", "qwen3-8b", "qwen3-14b", "Qwen3.8-27B"], models);
    }

    [Fact]
    public void A_model_added_later_goes_in_its_place_and_the_others_stay_put()
    {
        var models = new List<string> { "a-model", "c-model" };
        var c = models[1];
        MainWindowViewModel.InsertModel(models, "b-model");
        Assert.Equal(["a-model", "b-model", "c-model"], models);
        Assert.Same(c, models[2]);
    }
}
